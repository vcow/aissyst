using aissyst.core.Database;
using aissyst.core.LLMConnector.DTO;
using aissyst.core.Processor;
using Microsoft.Extensions.Logging;

namespace aissyst.core.LLMConnector.Model;

public sealed class SessionModel : IAsyncDisposable
{
	private sealed record ConfirmedState(int RequestId, string Summary, DateTime SummaryTimestamp, ILLMContinuation Continuation);
	private readonly ISessionSelector _sessionSelector;
	private readonly IDatabase _database;
	private readonly DialogCoordinator _dialog;
	private readonly ILLMConnector _connector;
	private readonly IApplicationSettings _settings;
	private readonly ILogger<SessionModel> _logger;
	private readonly SessionRecoveryOptions _recoveryOptions;
	private readonly Lock _sync = new();
	private readonly CancellationTokenSource _shutdown = new();
	private Task? _request;
	private bool _requestInProgress;
	private Task? _initialization;
	private Task? _recovery;
	private Task? _disposal;
	private ConfirmedState? _state;
	private string _instructions = string.Empty;
	private string _preview = string.Empty;
	private SessionContextItem[] _context = [];
	private bool _isReady;
	private bool _recoveryRequested;
	private static int _requestId;

	public SessionModel(ISessionSelector sessionSelector, IDatabase database, DialogCoordinator dialog,
		ILLMConnector connector, IApplicationSettings settings, ILogger<SessionModel> logger,
		SessionRecoveryOptions? recoveryOptions = null)
	{
		_sessionSelector = sessionSelector;
		_database = database;
		_dialog = dialog;
		_connector = connector;
		_settings = settings;
		_logger = logger;
		_recoveryOptions = recoveryOptions ?? new SessionRecoveryOptions();
		ArgumentOutOfRangeException.ThrowIfLessThan(_recoveryOptions.Attempts, 1);
		ArgumentOutOfRangeException.ThrowIfLessThan(_recoveryOptions.Delay, TimeSpan.Zero);
		_connector.ConnectionLost += OnConnectionLost;
	}

	public bool IsReady
	{
		get
		{
			lock (_sync)
			{
				return _isReady;
			}
		}
	}

	public Task InitializeAsync(CancellationToken cancellationToken = default)
	{
		lock (_sync)
		{
			ObjectDisposedException.ThrowIf(_disposal != null, this);
			cancellationToken.ThrowIfCancellationRequested();
			if (_isReady)
			{
				throw new InvalidOperationException("The session has already been initialized.");
			}
			if (_recovery is { IsCompleted: false })
			{
				throw new InvalidOperationException("The session is being recovered.");
			}
			if (_initialization == null || _initialization.IsCompleted)
			{
				_initialization = InitializeCoreAsync(cancellationToken);
			}
			return _initialization.WaitAsync(cancellationToken);
		}
	}

	private async Task InitializeCoreAsync(CancellationToken cancellationToken)
	{
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
		try
		{
			var id = _sessionSelector.GetSelectedSessionId();
			var context = await _database.GetSessionAsync(id, linked.Token);
			var instructions = await _database.GetSessionInitialInstructionsAsync(id, linked.Token);
			await _dialog.InitializeAsync(linked.Token);
			lock (_sync)
			{
				_preview = context.Preview;
				_context = context.Context.Select(record => new SessionContextItem { Topic = record.Topic, Body = record.Body }).ToArray();
				_instructions = instructions;
			}
			await InitializeRemoteSessionAsync(linked.Token);
		}
		catch (OperationCanceledException) when (linked.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception)
		{
			_logger.LogError(exception, "Failed to initialize the session model.");
			throw;
		}
	}

	private async Task InitializeRemoteSessionAsync(CancellationToken cancellationToken)
	{
		Session session;
		lock (_sync)
		{
			session = CreateSession();
		}
		var result = await _connector.InitializeSession(_instructions, session, cancellationToken);
		var accepted = false;
		try
		{
			ValidateResponse(result.Payload, session.RequestId, session.Dialog.LastOrDefault()?.Timestamp, null);
			if (result.Payload.Message.Length != 0 || (session.Dialog.Count == 0 && result.Payload.Summary.Length != 0))
			{
				throw new InvalidDataException("Unexpected content in the session initialization response.");
			}
			lock (_sync)
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (!result.Continuation.IsValid)
				{
					throw new IOException("The connection closed during session initialization.");
				}
				_state?.Continuation.Dispose();
				_state = new ConfirmedState(session.RequestId, result.Payload.Summary,
					result.Payload.SummaryTimestamp ?? DateTime.MinValue, result.Continuation);
				_isReady = true;
				accepted = true;
			}
		}
		finally
		{
			if (!accepted)
			{
				result.Continuation.Dispose();
			}
		}
	}

	public Task<DialogResponse?> SendRequestAsync(RequestType request, CancellationToken cancellationToken = default)
	{
		lock (_sync)
		{
			EnsureReady();
			cancellationToken.ThrowIfCancellationRequested();
			if (_requestInProgress || _connector.IsBusy)
			{
				throw new InvalidOperationException("The LLM connector is busy. Wait for the current request to finish.");
			}
			if (!Enum.IsDefined(request))
			{
				throw new ArgumentOutOfRangeException(nameof(request), "The request type is not supported.");
			}
			var payload = new DialogRequest
			{
				RequestId = Interlocked.Increment(ref _requestId), Request = request, Summary = _state!.Summary,
				RawRecords = Msg2DTO(_dialog.GetDialog(_state.SummaryTimestamp))
			};
			var parent = _state.Continuation.Retain();
			_requestInProgress = true;
			var operation = SendCoreAsync(payload, parent, cancellationToken);
			_request = operation;
			return operation;
		}
	}

	private async Task<DialogResponse?> SendCoreAsync(DialogRequest request, ILLMContinuation parent, CancellationToken cancellationToken)
	{
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
		LLMResponse? result = null;
		var accepted = false;
		try
		{
			result = await _connector.SendRequest(request, parent, linked.Token);
			lock (_sync)
			{
				linked.Token.ThrowIfCancellationRequested();
				if (!_isReady || _state == null || !result.Continuation.IsValid || !ReferenceEquals(parent.ConnectionId, _state.Continuation.ConnectionId)
					|| !ReferenceEquals(result.Continuation.ConnectionId, parent.ConnectionId) || request.RequestId <= _state.RequestId)
				{
					return null;
				}
				ValidateResponse(result.Payload, request.RequestId, request.RawRecords.LastOrDefault()?.Timestamp, request.Summary);
				var old = _state;
				_state = new ConfirmedState(request.RequestId, result.Payload.Summary,
					result.Payload.SummaryTimestamp ?? old.SummaryTimestamp, result.Continuation);
				accepted = true;
				old.Continuation.Dispose();
				return result.Payload;
			}
		}
		catch (OperationCanceledException) when (linked.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception)
		{
			_logger.LogError(exception, "LLM assistance request {RequestId} failed.", request.RequestId);
			throw;
		}
		finally
		{
			parent.Dispose();
			if (!accepted)
			{
				result?.Continuation.Dispose();
			}
			lock (_sync)
			{
				_requestInProgress = false;
			}
		}
	}

	private static void ValidateResponse(DialogResponse response, int requestId, DateTime? expectedTimestamp, string? previousSummary)
	{
		if (response.RequestId != requestId)
		{
			throw new InvalidDataException("The LLM response request_id does not match the submitted request.");
		}
		if (!string.IsNullOrWhiteSpace(response.Error))
		{
			throw new InvalidOperationException($"The model reported an error: {response.Error}");
		}
		if (response.Summary == null || response.Message == null || response.SummaryTimestamp != expectedTimestamp
			|| (expectedTimestamp.HasValue && string.IsNullOrWhiteSpace(response.Summary))
			|| (!string.IsNullOrEmpty(previousSummary) && string.IsNullOrWhiteSpace(response.Summary))
			|| (!expectedTimestamp.HasValue && previousSummary != null && response.Summary != previousSummary))
		{
			throw new InvalidDataException("The LLM response does not match the submitted dialog state.");
		}
	}

	public Session GetSession()
	{
		lock (_sync)
		{
			EnsureReady();
			return CreateSession();
		}
	}

	private Session CreateSession() => new()
	{
		RequestId = Interlocked.Increment(ref _requestId), Preview = _preview,
		Context = _context.Select(item => new SessionContextItem { Topic = item.Topic, Body = item.Body }).ToArray(),
		Dialog = Msg2DTO(_dialog.GetDialog(DateTime.MinValue)), UseMdFormat = _settings.UseMarkdownFormat
	};

	private static DialogItem[] Msg2DTO(IEnumerable<DialogMessage> messages) => messages.Select(message => new DialogItem
	{
		Message = message.Text, Timestamp = message.Timestamp,
		Role = message.Source switch
		{
			AudioSourceType.Microphone => SenderRole.Interviewee,
			AudioSourceType.Application => SenderRole.Interviewer,
			_ => SenderRole.Unknown
		}
	}).ToArray();

	private void OnConnectionLost(object? sender, EventArgs args)
	{
		lock (_sync)
		{
			if (_disposal != null || _state == null)
			{
				return;
			}
			_isReady = false;
			if (_recovery is { IsCompleted: false })
			{
				_recoveryRequested = true;
				return;
			}
			_recovery = Task.Run(RecoverAsync);
		}
	}

	private async Task RecoverAsync()
	{
		var restored = false;
		try
		{
			for (var attempt = 1; ; attempt++)
			{
				try
				{
					lock (_sync)
					{
						_recoveryRequested = false;
					}
					await Task.Delay(_recoveryOptions.Delay, _shutdown.Token);
					await _connector.ReconnectAsync(_shutdown.Token);
					await _dialog.ReloadAsync(_shutdown.Token);
					await InitializeRemoteSessionAsync(_shutdown.Token);
					lock (_sync)
					{
						if (!_isReady || _state == null || !_state.Continuation.IsValid)
						{
							throw new IOException("The connection closed while completing session recovery.");
						}
						restored = true;
					}
					_logger.LogInformation("The LLM connection and session have been restored. You can submit a new request.");
					return;
				}
				catch (Exception exception) when (!_shutdown.IsCancellationRequested)
				{
					_logger.LogWarning(exception, "LLM recovery attempt {Attempt} failed.", attempt);
					if (attempt >= _recoveryOptions.Attempts || exception is UnauthorizedAccessException or InvalidDataException)
					{
						throw;
					}
				}
			}
		}
		catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
		{
		}
		catch (Exception exception)
		{
			_logger.LogError(exception, "LLM recovery failed. Restart the application to reconnect.");
		}
		finally
		{
			lock (_sync)
			{
				if (restored && _recoveryRequested && _disposal == null && !_shutdown.IsCancellationRequested)
				{
					_recoveryRequested = false;
					_recovery = Task.Run(RecoverAsync);
				}
			}
		}
	}

	private void EnsureReady()
	{
		ObjectDisposedException.ThrowIf(_disposal != null, this);
		if (!_isReady)
		{
			throw new InvalidOperationException("The LLM session is not ready. Retry after recovery.");
		}
	}

	public ValueTask DisposeAsync()
	{
		lock (_sync)
		{
			return new ValueTask(_disposal ??= DisposeCoreAsync());
		}
	}

	private async Task DisposeCoreAsync()
	{
		_isReady = false;
		_connector.ConnectionLost -= OnConnectionLost;
		await _shutdown.CancelAsync();
		if (_initialization != null)
		{
			try { await _initialization; }
			catch (Exception) { /* Initialization already logs failures. */ }
		}
		if (_recovery != null)
		{
			await _recovery;
		}
		Task? request;
		lock (_sync)
		{
			request = _request;
		}
		if (request != null)
		{
			try { await request; }
			catch (Exception) { /* The request already logs failures. */ }
		}
		_state?.Continuation.Dispose();
		_shutdown.Dispose();
	}
}
