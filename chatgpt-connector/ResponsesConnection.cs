using System.Text.Json;
using aissyst.core.LLMConnector.DTO;
using Microsoft.Extensions.Logging;

namespace chatgpt_connector;

internal sealed class ResponsesConnection : IAsyncDisposable
{
	private readonly Func<CancellationToken, Task<string>> _getToken;
	private readonly Func<IResponsesSocket> _createSocket;
	private readonly string _model;
	private readonly Func<DateTimeOffset> _tokenExpiry;
	private DateTimeOffset _renewAt;
	private Exception? _connectionFault;
	private readonly ChatGPTConnectorOptions _options;
	private readonly ILogger _logger;
	private readonly CancellationTokenSource _shutdown = new();
	private readonly SemaphoreSlim _operations = new(1, 1);
	private readonly Lock _sync = new();
	private IResponsesSocket? _socket;
	private Task? _reader;
	private Task? _maintenance;
	private Task? _recovery;
	private Task? _disposal;
	private TaskCompletionSource<JsonElement>? _pending;
	private string? _activeResponseId;
	private string? _confirmedResponseId;
	private string? _instructions;
	private Func<Session>? _getSession;
	private Action<Session, DialogResponse>? _acceptRecovery;
	private Func<DialogResponse, bool>? _acceptResponse;
	private bool _initialized;
	private bool _recovering;
	private Exception? _unavailable;

	internal ResponsesConnection(string model, Func<CancellationToken, Task<string>> getToken,
		ChatGPTConnectorOptions options, ILogger logger, Func<IResponsesSocket>? createSocket = null, Func<DateTimeOffset>? tokenExpiry = null)
	{
		_model = model;
		_tokenExpiry = tokenExpiry ?? (() => DateTimeOffset.MaxValue);
		_getToken = getToken;
		_options = options;
		_logger = logger;
		_createSocket = createSocket ?? (() => new ResponsesSocket());
	}

	internal void Configure(string instructions, Func<Session> getSession,
		Action<Session, DialogResponse> acceptRecovery, Func<DialogResponse, bool> acceptResponse)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(instructions);
		lock (_sync)
		{
			ObjectDisposedException.ThrowIf(_disposal != null, this);
			_instructions = instructions;
			_getSession = getSession;
			_acceptRecovery = acceptRecovery;
			_acceptResponse = acceptResponse;
		}
	}

	internal async Task<DialogResponse> InitializeSessionAsync(Session session, CancellationToken cancellationToken)
	{
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
		await _operations.WaitAsync(linked.Token);
		try
		{
			if (_initialized)
			{
				throw new InvalidOperationException("The LLM session has already been initialized.");
			}
			await ConnectAsync(linked.Token);
			var result = await InitializeChainAsync(session, linked.Token);
			linked.Token.ThrowIfCancellationRequested();
			lock (_sync)
			{
				_initialized = true;
				if (_connectionFault != null)
				{
					ScheduleRecovery(_connectionFault);
				}
			}
			_maintenance = MaintainAsync();
			_logger.LogInformation("The LLM session is ready.");
			return result;
		}
		catch (Exception exception)
		{
			Disconnect();
			_logger.LogError("LLM session initialization failed ({Reason}).", Describe(exception));
			throw;
		}
		finally
		{
			_operations.Release();
		}
	}

	internal Task<DialogResponse> SendAsync(DialogRequest request, CancellationToken cancellationToken)
	{
		// Snapshot mutable DTOs before queuing. Cancellation ends the caller's wait, not the server response.
		var snapshot = JsonSerializer.Deserialize<DialogRequest>(JsonSerializer.Serialize(request))!;
		IResponsesSocket? connection;
		lock (_sync)
		{
			ObjectDisposedException.ThrowIf(_disposal != null, this);
			if (_recovering || _unavailable != null)
			{
				throw new InvalidOperationException("The LLM connection is not ready. Retry after recovery.");
			}
			connection = _socket;
		}
		var operation = SendCoreAsync(snapshot, connection, cancellationToken);
		_ = ObserveAsync(operation);
		return operation.WaitAsync(cancellationToken);
	}

	private async Task<DialogResponse> SendCoreAsync(DialogRequest request, IResponsesSocket? connection, CancellationToken cancellationToken)
	{
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
		await _operations.WaitAsync(linked.Token);
		try
		{
			lock (_sync)
			{
				ObjectDisposedException.ThrowIf(_disposal != null, this);
				if (!_initialized || _recovering || _unavailable != null || !ReferenceEquals(connection, _socket))
				{
					throw new InvalidOperationException("The LLM connection is not ready. Retry after recovery.");
				}
			}
			cancellationToken.ThrowIfCancellationRequested();
			var response = await ExchangeAsync("user", JsonSerializer.Serialize(request), _confirmedResponseId, true, _shutdown.Token);
			var result = ResponsesProtocol.Parse(response, request.RequestId,
				request.RawRecords.LastOrDefault()?.Timestamp, request.Summary, false);
			cancellationToken.ThrowIfCancellationRequested();
			if (string.IsNullOrWhiteSpace(result.Error))
			{
				if (!_shutdown.IsCancellationRequested && _acceptResponse!(result))
				{
					_confirmedResponseId = response.GetProperty("id").GetString();
				}
				else
				{
					throw new OperationCanceledException("The LLM response belongs to an obsolete request.");
				}
			}
			else
			{
				_logger.LogError("The model reported an error for request {RequestId}.", request.RequestId);
			}
			return result;
		}
		catch (Exception exception)
		{
			if (exception is not OperationCanceledException)
			{
				_logger.LogError("LLM request {RequestId} failed ({Reason}).", request.RequestId, Describe(exception));
			}
			// An unaccepted completed response can evict the confirmed cache entry. Recovery replays real dialog.
			if (!_shutdown.IsCancellationRequested && exception is not InvalidOperationException)
			{
				ScheduleRecovery(exception);
			}
			throw;
		}
		finally
		{
			_operations.Release();
		}
	}

	private async Task ConnectAsync(CancellationToken cancellationToken)
	{
		Disconnect();
		if (_reader != null)
		{
			await _reader;
		}
		var socket = _createSocket();
		try
		{
			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeout.CancelAfter(_options.ConnectionTimeout);
			await socket.ConnectAsync(await _getToken(timeout.Token), timeout.Token);
			var lifetime = _tokenExpiry() - DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1);
			lifetime = lifetime < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : lifetime;
			lock (_sync)
			{
				_renewAt = DateTimeOffset.UtcNow + (lifetime < _options.ConnectionLifetime ? lifetime : _options.ConnectionLifetime);
				_socket = socket;
				_connectionFault = null;
			}
			_reader = ReadAsync(socket);
			_logger.LogInformation("Connected to the Responses WebSocket.");
		}
		catch
		{
			socket.Dispose();
			throw;
		}
	}

	private async Task<DialogResponse> InitializeChainAsync(Session session, CancellationToken cancellationToken)
	{
		var instructions = _instructions ?? throw new InvalidOperationException("Initial LLM instructions are not configured.");
		var warmup = await ExchangeAsync("developer", instructions, null, false, cancellationToken);
		var response = await ExchangeAsync("user", JsonSerializer.Serialize(session),
			warmup.GetProperty("id").GetString(), true, cancellationToken);
		var result = ResponsesProtocol.Parse(response, session.RequestId, session.Dialog.LastOrDefault()?.Timestamp, null, true);
		if (!string.IsNullOrWhiteSpace(result.Error))
		{
			throw new InvalidDataException("The model rejected session initialization.");
		}
		_confirmedResponseId = response.GetProperty("id").GetString();
		return result;
	}

	private async Task<JsonElement> ExchangeAsync(string role, string text, string? previousId, bool generate,
		CancellationToken cancellationToken)
	{
		var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
		IResponsesSocket socket;
		lock (_sync)
		{
			socket = _socket ?? throw new IOException("The Responses WebSocket is disconnected.");
			_pending = completion;
			_activeResponseId = null;
		}
		try
		{
			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeout.CancelAfter(_options.ResponseTimeout);
			await socket.SendAsync(ResponsesProtocol.Create(_model, role, text, previousId, generate), timeout.Token);
			return await completion.Task.WaitAsync(timeout.Token);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			throw new TimeoutException("Timed out waiting for the Responses API.");
		}
		finally
		{
			lock (_sync)
			{
				if (ReferenceEquals(_pending, completion))
				{
					_pending = null;
				}
			}
		}
	}

	private async Task ReadAsync(IResponsesSocket socket)
	{
		try
		{
			while (!_shutdown.IsCancellationRequested)
			{
				using var document = JsonDocument.Parse(await socket.ReceiveAsync(_shutdown.Token));
				var item = document.RootElement;
				var type = item.GetProperty("type").GetString();
				if (item.TryGetProperty("stream_id", out var lane) && lane.GetString() != "interview")
				{
					continue;
				}
				lock (_sync)
				{
					if (!ReferenceEquals(_socket, socket))
					{
						return;
					}
					if (type == "error")
					{
						var error = item.GetProperty("error");
						var code = error.TryGetProperty("code", out var codeValue) && codeValue.ValueKind == JsonValueKind.String
							? codeValue.GetString()! : "unspecified_error";
						var exception = new ResponsesApiException(code,
							item.TryGetProperty("status", out var status) && status.TryGetInt32(out var statusValue) ? statusValue : null);
						_logger.LogError("Responses API rejected an operation ({Code}).", code);
						if (_pending != null)
						{
							_pending.TrySetException(exception);
						}
						else
						{
							throw exception;
						}
						continue;
					}
					if (!item.TryGetProperty("response", out var response))
					{
						continue;
					}
					var id = response.GetProperty("id").GetString();
					if (string.IsNullOrWhiteSpace(id))
					{
						throw new InvalidDataException("The Responses API returned an empty response ID.");
					}
					if (_activeResponseId != null && _activeResponseId != id)
					{
						throw new InvalidDataException("The Responses API returned an unexpected response ID.");
					}
					_activeResponseId = id;
					if (type == "response.completed")
					{
						if (response.GetProperty("status").GetString() != "completed")
						{
							throw new InvalidDataException("The response did not complete successfully.");
						}
						_pending?.TrySetResult(response.Clone());
					}
					else if (type is "response.failed" or "response.incomplete")
					{
						_pending?.TrySetException(new InvalidDataException($"Responses API event: {type}."));
					}
				}
			}
		}
		catch (Exception exception)
		{
			lock (_sync)
			{
				if (!ReferenceEquals(_socket, socket))
				{
					return;
				}
				_connectionFault = exception;
				_pending?.TrySetException(exception);
				if (!_shutdown.IsCancellationRequested)
				{
					_logger.LogWarning("The Responses connection was interrupted ({Reason}). Recovering; the interrupted request will not be retried.", Describe(exception));
					ScheduleRecovery(exception);
				}
			}
		}
	}

	private void ScheduleRecovery(Exception reason)
	{
		lock (_sync)
		{
			if (!_initialized || _shutdown.IsCancellationRequested || _recovering || _unavailable != null)
			{
				return;
			}
			_recovering = true;
			_recovery = Task.Run(() => RecoverAsync(reason));
		}
	}

	private async Task RecoverAsync(Exception reason)
	{
		try
		{
			await _operations.WaitAsync(_shutdown.Token);
			try
			{
				if (reason is ResponsesApiException { IsAuthorizationError: true })
				{
					throw reason;
				}
				for (var attempt = 1; ; attempt++)
				{
					try
					{
						await Task.Delay(_options.ReconnectDelay, _shutdown.Token);
						await ConnectAsync(_shutdown.Token);
						var session = _getSession!();
						var response = await InitializeChainAsync(session, _shutdown.Token);
						lock (_sync)
						{
							if (_connectionFault != null)
							{
								throw new IOException("The connection closed during session recovery.", _connectionFault);
							}
							_shutdown.Token.ThrowIfCancellationRequested();
							_acceptRecovery!(session, response);
							_recovering = false;
							_unavailable = null;
						}
						_logger.LogInformation("The LLM connection and session have been restored. You can submit a new request.");
						return;
					}
					catch (Exception exception) when (!_shutdown.IsCancellationRequested)
					{
						_logger.LogWarning("LLM reconnection attempt {Attempt} failed ({Reason}).", attempt, Describe(exception));
						if (attempt >= _options.ReconnectAttempts || exception is ResponsesApiException { IsAuthorizationError: true }
							|| exception is UnauthorizedAccessException or InvalidDataException or JsonException)
						{
							throw;
						}
					}
				}
			}
			finally
			{
				_operations.Release();
			}
		}
		catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
		{
		}
		catch (Exception exception)
		{
			Disconnect();
			lock (_sync)
			{
				_unavailable = exception;
				_recovering = false;
			}
			_logger.LogError("LLM recovery failed ({Reason}). Restart the application to reconnect.", Describe(exception));
		}
	}

	private async Task MaintainAsync()
	{
		try
		{
			while (!_shutdown.IsCancellationRequested)
			{
				TimeSpan delay;
				lock (_sync)
				{
					delay = _renewAt - DateTimeOffset.UtcNow;
				}
				await Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.FromSeconds(1), _shutdown.Token);
				ScheduleRecovery(new IOException("The connection or access token must be renewed."));
			}
		}
		catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
		{
		}
	}

	private static string Describe(Exception exception) => exception switch
	{
		JsonException => "The server returned invalid JSON",
		ResponsesApiException or InvalidDataException or TimeoutException or UnauthorizedAccessException => exception.Message,
		_ => exception.GetType().Name
	};

	private static async Task ObserveAsync(Task operation)
	{
		try
		{
			await operation;
		}
		catch (Exception)
		{
			// SendCoreAsync logs failures even when the caller has already stopped waiting.
		}
	}

	private void Disconnect()
	{
		lock (_sync)
		{
			var socket = _socket;
			_socket = null;
			socket?.Dispose();
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
		await _shutdown.CancelAsync();
		Disconnect();
		if (_maintenance != null)
		{
			await _maintenance;
		}
		if (_recovery != null)
		{
			await _recovery;
		}
		if (_reader != null)
		{
			await _reader;
		}
		await _operations.WaitAsync();
		_operations.Release();
	}
}
