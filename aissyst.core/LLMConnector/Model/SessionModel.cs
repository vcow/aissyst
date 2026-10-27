using aissyst.core.Database;
using aissyst.core.LLMConnector.DTO;
using aissyst.core.Processor;
using Microsoft.Extensions.Logging;

namespace aissyst.core.LLMConnector.Model;

public class SessionModel : IAsyncDisposable
{
	private readonly ISessionSelector _sessionSelector;
	private readonly IDatabase _database;
	private readonly ITextProcessor _textProcessor;
	private readonly ILLMConnector _llmConnector;
	private readonly IApplicationSettings _settings;
	private readonly ILogger<SessionModel> _logger;
	private readonly Lock _sync = new();
	private readonly CancellationTokenSource _shutdown = new();
	private Task? _initialization;
	private Task? _disposal;
	private string _preview = string.Empty;
	private string _summary = string.Empty;
	private DateTime _summaryTimestamp = DateTime.MinValue;
	private SessionContextItem[] _context = [];
	private bool _isReady;
	private static int _requestId;
	private int _latestRequestId;
	private int _lastAppliedRequestId;
	private DateTime? _pendingSummaryTimestamp;

	public SessionModel(ISessionSelector sessionSelector, IDatabase database, ITextProcessor textProcessor,
		ILLMConnector llmConnector, IApplicationSettings settings, ILogger<SessionModel> logger)
	{
		_sessionSelector = sessionSelector;
		_database = database;
		_textProcessor = textProcessor;
		_llmConnector = llmConnector;
		_settings = settings;
		_logger = logger;
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
		var token = linked.Token;
		try
		{
			var sessionId = _sessionSelector.GetSelectedSessionId();
			var sessionContext = await _database.GetSessionAsync(sessionId, token);
			await _textProcessor.InitializeAsync(token);
			var session = new Session
			{
				Preview = sessionContext.Preview,
				Context = sessionContext.Context.Select(record => new SessionContextItem
					{ Topic = record.Topic, Body = record.Body }).ToArray(),
				Dialog = Msg2DTO(_textProcessor.GetDialog(DateTime.MinValue)),
				UseMdFormat = _settings.UseMarkdownFormat
			};
			var response = await _llmConnector.InitializeSession(session, token);
			if (!string.IsNullOrWhiteSpace(response.Error))
			{
				throw new InvalidOperationException($"LLM session initialization failed: {response.Error}");
			}
			ValidateSummary(response, session.Dialog.LastOrDefault()?.Timestamp);
			lock (_sync)
			{
				token.ThrowIfCancellationRequested();
				_preview = session.Preview;
				_context = session.Context.Select(item => new SessionContextItem
					{ Topic = item.Topic, Body = item.Body }).ToArray();
				_summary = response.Summary;
				_summaryTimestamp = response.SummaryTimestamp ?? DateTime.MinValue;
				_isReady = true;
			}
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception)
		{
			_logger.LogError(exception, "Failed to initialize the session model.");
			throw;
		}
	}

	public void Reset(DialogResponse response)
	{
		lock (_sync)
		{
			EnsureReady();
			if (response.RequestId != _latestRequestId || response.RequestId <= _lastAppliedRequestId)
			{
				_logger.LogDebug("Ignored an obsolete or duplicate LLM response for request {RequestId}.", response.RequestId);
				return;
			}
			if (!string.IsNullOrWhiteSpace(response.Error))
			{
				_logger.LogError("LLM request failed: {Error}", response.Error);
				return;
			}
			ValidateSummary(response, _pendingSummaryTimestamp);
			if (_summary.Length != 0 && string.IsNullOrWhiteSpace(response.Summary))
			{
				throw new InvalidDataException("The LLM response cannot erase an existing dialog summary.");
			}
			_summary = response.Summary;
			// An empty raw_records list does not reset the confirmed dialog boundary.
			_summaryTimestamp = response.SummaryTimestamp ?? _summaryTimestamp;
			_lastAppliedRequestId = response.RequestId;
		}
	}

	private static void ValidateSummary(DialogResponse response, DateTime? expectedTimestamp)
	{
		if (response.Summary == null)
		{
			throw new InvalidDataException("The LLM summary must be a string.");
		}
		if (response.SummaryTimestamp != expectedTimestamp)
		{
			throw new InvalidDataException("The LLM summary timestamp does not match the submitted dialog boundary.");
		}
		if (expectedTimestamp.HasValue && string.IsNullOrWhiteSpace(response.Summary))
		{
			throw new InvalidDataException("The LLM returned an empty summary for a non-empty dialog.");
		}
	}

	public Session GetSession()
	{
		lock (_sync)
		{
			EnsureReady();
			return new Session
			{
				Preview = _preview,
				Context = _context.Select(item => new SessionContextItem { Topic = item.Topic, Body = item.Body }).ToArray(),
				Dialog = Msg2DTO(_textProcessor.GetDialog(DateTime.MinValue)),
				UseMdFormat = _settings.UseMarkdownFormat
			};
		}
	}

	private static DialogItem[] Msg2DTO(IEnumerable<DialogMessage> messages) => messages.Select(message => new DialogItem
	{
		Message = message.Text,
		Timestamp = message.Timestamp,
		Role = message.Source switch
		{
			AudioSourceType.Microphone => SenderRole.Interviewee,
			AudioSourceType.Application => SenderRole.Interviewer,
			_ => SenderRole.Unknown
		}
	}).ToArray();

	public DialogRequest GetRequest(RequestType request)
	{
		lock (_sync)
		{
			EnsureReady();
			if (!Enum.IsDefined(request))
			{
				throw new ArgumentOutOfRangeException(nameof(request), "The request type is not supported.");
			}
			var records = Msg2DTO(_textProcessor.GetDialog(_summaryTimestamp));
			_latestRequestId = Interlocked.Increment(ref _requestId);
			_pendingSummaryTimestamp = records.LastOrDefault()?.Timestamp;
			return new DialogRequest { RequestId = _latestRequestId, Request = request, Summary = _summary, RawRecords = records };
		}
	}

	private void EnsureReady()
	{
		ObjectDisposedException.ThrowIf(_disposal != null, this);
		if (!_isReady)
		{
			throw new InvalidOperationException("The session has not been initialized.");
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
		await _shutdown.CancelAsync();
		try
		{
			if (_initialization != null)
			{
				await _initialization;
			}
		}
		catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
		{
		}
		catch (Exception)
		{
			// InitializeCoreAsync already logged initialization failures.
		}
		finally
		{
			_shutdown.Dispose();
		}
	}
}
