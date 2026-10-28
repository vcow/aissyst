using System.Text.Json;
using aissyst.core.LLMConnector.DTO;
using Microsoft.Extensions.Logging;

namespace chatgpt_connector;

internal sealed class ResponsesConnection : IAsyncDisposable
{
	private readonly Func<CancellationToken, Task<string>> _getToken;
	private readonly Func<IResponsesSocket> _createSocket;
	private readonly string _model;
	private Exception? _connectionFault;
	private readonly ChatGPTConnectorOptions _options;
	private readonly ILogger _logger;
	private readonly CancellationTokenSource _shutdown = new();
	private readonly SemaphoreSlim _operations = new(1, 1);
	private readonly Lock _sync = new();
	private IResponsesSocket? _socket;
	private Task? _reader;
	private Task? _recovery;
	private Task? _disposal;
	private sealed class SendTimeoutException() : TimeoutException("Timed out sending a Responses API request.");
	private sealed class Pending(string? parentStream)
	{
		internal readonly TaskCompletionSource<JsonElement> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
		internal readonly string? ParentStream = parentStream;
		internal string? ResponseId;
	}
	private readonly Dictionary<string, Pending> _pending = [];
	private readonly HashSet<Task> _requests = [];
	private readonly SemaphoreSlim _requestSlots = new(15, 15);
	private string _confirmedStream = "interview";
	private string? _confirmedResponseId;
	private string? _instructions;
	private Func<CancellationToken, Task<Session>>? _getSession;
	private Action<Session, DialogResponse>? _acceptRecovery;
	private Func<DialogResponse, bool>? _acceptResponse;
	private bool _initialized;
	private bool _recovering;
	private Exception? _unavailable;

	internal ResponsesConnection(string model, Func<CancellationToken, Task<string>> getToken,
		ChatGPTConnectorOptions options, ILogger logger, Func<IResponsesSocket>? createSocket = null)
	{
		_model = model;
		_getToken = getToken;
		_options = options;
		_logger = logger;
		_createSocket = createSocket ?? (() => new ResponsesSocket());
	}

	internal void Configure(string instructions, Func<CancellationToken, Task<Session>> getSession,
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
		lock (_sync)
		{
			_requests.Add(operation);
		}
		_ = ObserveRequestAsync(operation);
		return operation.WaitAsync(cancellationToken);
	}

	private async Task<DialogResponse> SendCoreAsync(DialogRequest request, IResponsesSocket? connection, CancellationToken cancellationToken)
	{
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
		if (!await _requestSlots.WaitAsync(_options.ResponseTimeout, linked.Token))
		{
			throw new TimeoutException("All Responses request slots are busy.");
		}
		string? lane = null;
		Pending? pending = null;
		try
		{
			await _operations.WaitAsync(linked.Token);
			try
			{
				string? parent;
				lock (_sync)
				{
					ObjectDisposedException.ThrowIf(_disposal != null, this);
					if (!_initialized || _recovering || _unavailable != null || !ReferenceEquals(connection, _socket))
					{
						throw new InvalidOperationException("The LLM connection is not ready. Retry after recovery.");
					}
					// Keep confirmed and referenced parent lanes intact while forks are in flight.
					lane = Enumerable.Range(0, 31).Select(index => "request_" + index).First(candidate =>
						candidate != _confirmedStream && !_pending.ContainsKey(candidate)
						&& !_pending.Values.Any(value => value.ParentStream == candidate));
					parent = _confirmedResponseId;
					pending = new Pending(_confirmedStream);
					_pending.Add(lane, pending);
				}
				using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
				timeout.CancelAfter(_options.ResponseTimeout);
				try
				{
					await connection!.SendAsync(ResponsesProtocol.Create(_model, "user", JsonSerializer.Serialize(request), parent, true, lane), timeout.Token);
				}
				catch (OperationCanceledException) when (!_shutdown.IsCancellationRequested)
				{
					throw new SendTimeoutException();
				}
			}
			finally
			{
				_operations.Release();
			}
			var response = await pending!.Completion.Task.WaitAsync(_options.ResponseTimeout, _shutdown.Token);
			var result = ResponsesProtocol.Parse(response, request.RequestId,
				request.RawRecords.LastOrDefault()?.Timestamp, request.Summary, false);
			cancellationToken.ThrowIfCancellationRequested();
			lock (_sync)
			{
				if (!_shutdown.IsCancellationRequested && !_recovering && ReferenceEquals(connection, _socket)
					&& string.IsNullOrWhiteSpace(result.Error) && _acceptResponse!(result))
				{
					_confirmedResponseId = response.GetProperty("id").GetString();
					_confirmedStream = lane!;
				}
			}
			if (!string.IsNullOrWhiteSpace(result.Error))
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
			if (!_shutdown.IsCancellationRequested && (exception is SendTimeoutException
				|| exception is ResponsesApiException { Code: "previous_response_not_found" } or ResponsesApiException { IsAuthorizationError: true }
				|| (exception is IOException && exception is not (InvalidDataException or ResponsesApiException))))
			{
				ScheduleRecovery(exception);
			}
			throw;
		}
		finally
		{
			if (lane != null && pending != null && !pending.Completion.Task.IsCompleted && !_shutdown.IsCancellationRequested)
			{
				// A timed-out response can still produce events. Reserve its lane until it ends.
				var drain = DrainAsync(lane, pending);
				lock (_sync)
				{
					_requests.Add(drain);
				}
				_ = ObserveRequestAsync(drain);
			}
			else
			{
				ReleaseRequest(lane, pending);
			}
		}
	}

	private async Task DrainAsync(string lane, Pending pending)
	{
		try
		{
			await pending.Completion.Task.WaitAsync(_shutdown.Token);
		}
		catch (Exception)
		{
			// The request already failed. Its late result must not be applied.
		}
		finally
		{
			ReleaseRequest(lane, pending);
		}
	}

	private void ReleaseRequest(string? lane, Pending? pending)
	{
		lock (_sync)
		{
			if (lane != null && _pending.TryGetValue(lane, out var current) && ReferenceEquals(current, pending))
			{
				_pending.Remove(lane);
			}
		}
		_requestSlots.Release();
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
			lock (_sync)
			{
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
		_confirmedStream = "interview";
		return result;
	}

	private async Task<JsonElement> ExchangeAsync(string role, string text, string? previousId, bool generate,
		CancellationToken cancellationToken)
	{
		var pending = new Pending(null);
		IResponsesSocket socket;
		lock (_sync)
		{
			socket = _socket ?? throw new IOException("The Responses WebSocket is disconnected.");
			_pending.Add("interview", pending);
		}
		try
		{
			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeout.CancelAfter(_options.ResponseTimeout);
			await socket.SendAsync(ResponsesProtocol.Create(_model, role, text, previousId, generate), timeout.Token);
			return await pending.Completion.Task.WaitAsync(timeout.Token);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			throw new TimeoutException("Timed out waiting for the Responses API.");
		}
		finally
		{
			lock (_sync)
			{
				if (_pending.TryGetValue("interview", out var current) && ReferenceEquals(current, pending))
				{
					_pending.Remove("interview");
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
				var lane = item.TryGetProperty("stream_id", out var stream) ? stream.GetString() : null;
				lock (_sync)
				{
					if (!ReferenceEquals(_socket, socket))
					{
						return;
					}
					var pending = lane != null ? _pending.GetValueOrDefault(lane) : null;
					if (type == "error")
					{
						var error = item.GetProperty("error");
						var code = error.TryGetProperty("code", out var codeValue) && codeValue.ValueKind == JsonValueKind.String
							? codeValue.GetString()! : "unspecified_error";
						var exception = new ResponsesApiException(code,
							item.TryGetProperty("status", out var status) && status.TryGetInt32(out var statusValue) ? statusValue : null);
						_logger.LogError("Responses API rejected an operation ({Code}).", code);
						if (pending != null)
						{
							pending.Completion.TrySetException(exception);
						}
						else
						{
							throw exception;
						}
						continue;
					}
					if (pending == null || !item.TryGetProperty("response", out var response))
					{
						continue;
					}
					var id = response.GetProperty("id").GetString();
					if (string.IsNullOrWhiteSpace(id))
					{
						throw new InvalidDataException("The Responses API returned an empty response ID.");
					}
					if (pending.ResponseId != null && pending.ResponseId != id)
					{
						throw new InvalidDataException("The Responses API returned an unexpected response ID.");
					}
					pending.ResponseId = id;
					if (type == "response.completed")
					{
						if (response.GetProperty("status").GetString() != "completed")
						{
							throw new InvalidDataException("The response did not complete successfully.");
						}
						pending.Completion.TrySetResult(response.Clone());
					}
					else if (type is "response.failed" or "response.incomplete")
					{
						pending.Completion.TrySetException(new InvalidDataException($"Responses API event: {type}."));
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
				foreach (var pending in _pending.Values)
				{
					pending.Completion.TrySetException(exception);
				}
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
						var session = await _getSession!(_shutdown.Token);
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

	private static string Describe(Exception exception) => exception switch
	{
		JsonException => "The server returned invalid JSON",
		ResponsesApiException or InvalidDataException or TimeoutException or UnauthorizedAccessException => exception.Message,
		_ => exception.GetType().Name
	};

	private async Task ObserveRequestAsync(Task operation)
	{
		try
		{
			await operation;
		}
		catch (Exception)
		{
			// SendCoreAsync logs failures even when the caller has already stopped waiting.
		}
		finally
		{
			lock (_sync)
			{
				_requests.Remove(operation);
			}
		}
	}

	private void Disconnect()
	{
		lock (_sync)
		{
			var socket = _socket;
			_socket = null;
			foreach (var pending in _pending.Values)
			{
				pending.Completion.TrySetException(new IOException("The Responses WebSocket is disconnected."));
			}
			_pending.Clear();
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
		Task[] requests;
		lock (_sync)
		{
			requests = _requests.ToArray();
		}
		try
		{
			await Task.WhenAll(requests);
		}
		catch (Exception)
		{
			// Individual requests already report failures to their callers and the logger.
		}
	}
}
