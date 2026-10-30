using System.Text.Json;
using aissyst.core.LLMConnector;
using aissyst.core.LLMConnector.DTO;
using Microsoft.Extensions.Logging;

namespace chatgpt_connector;

// One active operation and one reader per connection. The caller owns confirmed application state.
internal sealed class ResponsesConnection : IAsyncDisposable
{
	private sealed class Pending
	{
		internal readonly TaskCompletionSource<JsonElement> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
		internal string? ResponseId;
		internal readonly SortedDictionary<int, JsonElement> OutputItems = [];

		internal JsonElement GetCompletedResponse(JsonElement response)
		{
			if (response.GetProperty("output").GetArrayLength() != 0 || OutputItems.Count == 0)
			{
				return response.Clone();
			}
			// Some servers send finalized items only in stream events, leaving the terminal output empty.
			var properties = response.EnumerateObject().ToDictionary(property => property.Name, property => (object)property.Value);
			properties["output"] = OutputItems.Values.ToArray();
			return JsonSerializer.SerializeToElement(properties);
		}
	}

	private sealed class Operation(CancellationTokenSource cancellation)
	{
		internal readonly CancellationTokenSource Cancellation = cancellation;
		internal readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
	}

	private sealed class Continuation(ResponsesConnection owner, IResponsesSocket socket, string responseId) : ILLMContinuation
	{
		private int _disposed;
		internal readonly ResponsesConnection Owner = owner;
		internal readonly IResponsesSocket Socket = socket;
		internal readonly string ResponseId = responseId;
		public object ConnectionId => Socket;
		public bool IsValid
		{
			get
			{
				lock (Owner._sync)
				{
					return Volatile.Read(ref _disposed) == 0 && ReferenceEquals(Socket, Owner._socket)
						&& Owner._connectionFault == null && Owner._disposal == null;
				}
			}
		}
		public ILLMContinuation Retain()
		{
			ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
			return new Continuation(Owner, Socket, ResponseId);
		}
		public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
	}

	private readonly Func<CancellationToken, Task<string>> _getToken;
	private readonly Func<IResponsesSocket> _createSocket;
	private readonly string _model;
	private readonly ChatGPTConnectorOptions _options;
	private readonly ILogger _logger;
	private readonly CancellationTokenSource _shutdown = new();
	private readonly SemaphoreSlim _cancelGate = new(1, 1);
	private readonly Lock _sync = new();
	private Operation? _active;
	private bool _cancelling;
	private Pending? _pending;
	private IResponsesSocket? _socket;
	private Task? _reader;
	private Task? _disposal;
	private Exception? _connectionFault;
	public event EventHandler? ConnectionLost;

	public bool IsBusy
	{
		get
		{
			lock (_sync)
			{
				return _active != null || _cancelling;
			}
		}
	}

	internal ResponsesConnection(string model, Func<CancellationToken, Task<string>> getToken,
		ChatGPTConnectorOptions options, ILogger logger, Func<IResponsesSocket>? createSocket = null)
	{
		_model = model;
		_getToken = getToken;
		_options = options;
		_logger = logger;
		_createSocket = createSocket ?? (() => new ResponsesSocket());
	}

	private Task<T> StartOperation<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
	{
		Operation operation;
		lock (_sync)
		{
			ObjectDisposedException.ThrowIf(_disposal != null || _shutdown.IsCancellationRequested, this);
			cancellationToken.ThrowIfCancellationRequested();
			if (_active != null || _cancelling)
			{
				throw new InvalidOperationException("The LLM connector is busy. Wait for the current request to finish.");
			}
			operation = new Operation(CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token));
			_active = operation;
		}
		return ExecuteAsync(operation, action);
	}

	private async Task<T> ExecuteAsync<T>(Operation operation, Func<CancellationToken, Task<T>> action)
	{
		try
		{
			return await action(operation.Cancellation.Token);
		}
		finally
		{
			lock (_sync)
			{
				_active = null;
				operation.Cancellation.Dispose();
				operation.Completion.TrySetResult();
			}
		}
	}

	internal async Task ReconnectAsync(CancellationToken cancellationToken)
	{
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
		while (true)
		{
			Task? active;
			lock (_sync)
			{
				active = _active?.Completion.Task;
			}
			if (active != null)
			{
				await active.WaitAsync(linked.Token);
			}
			await _cancelGate.WaitAsync(linked.Token);
			Task<bool> reconnect;
			try
			{
				lock (_sync)
				{
					if (_active != null)
					{
						continue;
					}
				}
				reconnect = StartOperation(async token =>
				{
					CheckAuthorization();
					await DisconnectAsync();
					await ConnectAsync(token);
					return true;
				}, linked.Token);
			}
			finally
			{
				_cancelGate.Release();
			}
			await reconnect;
			return;
		}
	}

	internal async Task CancelAsync(CancellationToken cancellationToken = default)
	{
		Operation? target;
		lock (_sync)
		{
			target = _active;
		}
		if (target == null || !await _cancelGate.WaitAsync(0, cancellationToken))
		{
			return;
		}
		try
		{
			Task cancellation;
			IResponsesSocket? socket;
			lock (_sync)
			{
				if (!ReferenceEquals(target, _active))
				{
					return;
				}
				_cancelling = true;
				socket = _socket;
				cancellation = target.Cancellation.CancelAsync();
			}
			_logger.LogInformation("Canceling the active Responses request and closing the WebSocket.");
			if (socket != null)
			{
				FailConnection(socket, new OperationCanceledException("The Responses request was canceled by the user."));
			}
			await cancellation;
			await target.Completion.Task;
			if (_reader != null)
			{
				await _reader;
			}
		}
		finally
		{
			lock (_sync)
			{
				_cancelling = false;
			}
			_cancelGate.Release();
		}
	}

	private void CheckAuthorization()
	{
		if (_connectionFault is ResponsesApiException { IsAuthorizationError: true } exception)
		{
			throw new UnauthorizedAccessException("ChatGPT authorization failed. Restart the application to sign in again.", exception);
		}
	}

	private async Task ConnectAsync(CancellationToken cancellationToken)
	{
		var socket = _createSocket();
		try
		{
			lock (_sync)
			{
				cancellationToken.ThrowIfCancellationRequested();
				_socket = socket;
				_connectionFault = null;
			}
			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeout.CancelAfter(_options.ConnectionTimeout);
			await socket.ConnectAsync(await _getToken(timeout.Token), timeout.Token);
			cancellationToken.ThrowIfCancellationRequested();
			_reader = ReadAsync(socket);
			_logger.LogInformation("Connected to the Responses WebSocket.");
		}
		catch (Exception exception)
		{
			FailConnection(socket, exception);
			socket.Dispose();
			if (exception is ResponsesApiException { IsAuthorizationError: true })
			{
				throw new UnauthorizedAccessException("ChatGPT authorization failed. Restart the application to sign in again.", exception);
			}
			throw;
		}
	}

	internal Task<LLMResponse> InitializeSessionAsync(string instructions, Session session, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(instructions);
		return StartOperation(async token =>
		{
			if (_socket == null || _connectionFault != null)
			{
				CheckAuthorization();
				await DisconnectAsync();
				await ConnectAsync(token);
			}
			var socket = _socket!;
			var warmup = await ExchangeAsync(socket, instructions, "developer", null, false, token);
			var response = await ExchangeAsync(socket, ResponsesProtocol.Serialize(session), "user",
				warmup.GetProperty("id").GetString(), true, token);
			return CreateResult(socket, response);
		}, cancellationToken);
	}

	internal Task<LLMResponse> SendAsync(DialogRequest request, ILLMContinuation continuation, CancellationToken cancellationToken)
	{
		if (continuation is not Continuation parent || !ReferenceEquals(parent.Owner, this))
		{
			throw new ArgumentException("The continuation belongs to another connector.", nameof(continuation));
		}
		return StartOperation(async token =>
		{
			using var lease = (Continuation)parent.Retain();
			var response = await ExchangeAsync(lease.Socket, ResponsesProtocol.Serialize(request), "user", lease.ResponseId, true, token);
			return CreateResult(lease.Socket, response);
		}, cancellationToken);
	}

	private LLMResponse CreateResult(IResponsesSocket socket, JsonElement response)
	{
		var payload = ResponsesProtocol.Parse(response);
		lock (_sync)
		{
			RequireSocket(socket);
			return new LLMResponse(payload, new Continuation(this, socket, response.GetProperty("id").GetString()!));
		}
	}

	private void RequireSocket(IResponsesSocket socket)
	{
		ObjectDisposedException.ThrowIf(_disposal != null || _shutdown.IsCancellationRequested, this);
		if (!ReferenceEquals(socket, _socket) || _connectionFault != null)
		{
			throw new IOException("The Responses continuation belongs to a disconnected socket.");
		}
	}

	private async Task<JsonElement> ExchangeAsync(IResponsesSocket socket, string text, string role, string? previousId,
		bool generate, CancellationToken cancellationToken)
	{
		var pending = new Pending();
		lock (_sync)
		{
			RequireSocket(socket);
			_pending = pending;
		}
		try
		{
			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeout.CancelAfter(_options.ResponseTimeout);
			try
			{
				var payload = ResponsesProtocol.Create(_model, role, text, previousId, generate);
				if (_options.LogProtocolPayloads)
				{
					_logger.LogInformation("Responses WebSocket SEND: {Payload}", payload);
				}
				await socket.SendAsync(payload, timeout.Token);
			}
			catch (Exception exception)
			{
				FailConnection(socket, exception);
				throw;
			}
			return await pending.Completion.Task.WaitAsync(_options.ResponseTimeout, cancellationToken);
		}
		catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
		{
			// Leaving an unfinished generation on the socket would permit overlapping requests.
			FailConnection(socket, exception);
			throw;
		}
		finally
		{
			lock (_sync)
			{
				_pending = null;
			}
		}
	}

	private async Task ReadAsync(IResponsesSocket socket)
	{
		try
		{
			while (!_shutdown.IsCancellationRequested)
			{
				var payload = await socket.ReceiveAsync(_shutdown.Token);
				if (_options.LogProtocolPayloads)
				{
					_logger.LogInformation("Responses WebSocket RECEIVE: {Payload}", payload);
				}
				using var document = JsonDocument.Parse(payload);
				var item = document.RootElement;
				var type = item.GetProperty("type").GetString();
				lock (_sync)
				{
					if (!ReferenceEquals(socket, _socket))
					{
						return;
					}
					var pending = _pending;
					if (type == "error")
					{
						var exception = LogApiError(item, type, pending?.ResponseId);
						if (pending == null || exception.IsAuthorizationError || exception.Code == "previous_response_not_found")
						{
							throw exception;
						}
						pending.Completion.TrySetException(exception);
						continue;
					}
					if (pending == null || pending.Completion.Task.IsCompleted)
					{
						continue;
					}
					if (type == "response.output_item.done")
					{
						pending.OutputItems[item.GetProperty("output_index").GetInt32()] = item.GetProperty("item").Clone();
						continue;
					}
					if (type is not ("response.created" or "response.in_progress" or "response.completed" or "response.failed" or "response.incomplete"))
					{
						continue;
					}
					var response = item.GetProperty("response");
					var id = response.GetProperty("id").GetString();
					if (string.IsNullOrWhiteSpace(id) || (pending.ResponseId != null && pending.ResponseId != id))
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
						pending.Completion.TrySetResult(pending.GetCompletedResponse(response));
					}
					else if (type is "response.failed" or "response.incomplete")
					{
						pending.Completion.TrySetException(LogApiError(item, type, id));
					}
				}
			}
		}
		catch (Exception exception)
		{
			FailConnection(socket, exception);
		}
	}

	private ResponsesApiException LogApiError(JsonElement item, string? eventType, string? responseId)
	{
		var exception = ResponsesProtocol.ParseError(item);
		_logger.LogError("Responses API request failed. Model: {Model}; Event: {EventType}; " +
			"Response: {ResponseId}; HTTP status: {Status}; Code: {Code}; Type: {ErrorType}; " +
			"Parameter: {Parameter}; Request ID: {RequestId}; Server message: {ServerMessage}",
			_model, eventType, responseId, exception.Status, exception.Code, exception.ErrorType,
			exception.Parameter, exception.RequestId, exception.ServerMessage);
		return exception;
	}

	private void FailConnection(IResponsesSocket socket, Exception exception)
	{
		bool notify;
		lock (_sync)
		{
			if (!ReferenceEquals(socket, _socket) || _connectionFault != null)
			{
				return;
			}
			_connectionFault = exception;
			_pending?.Completion.TrySetException(exception);
			notify = !_shutdown.IsCancellationRequested;
		}
		socket.Dispose();
		if (notify)
		{
			_logger.LogWarning(exception, "The Responses connection was interrupted.");
			ConnectionLost?.Invoke(this, EventArgs.Empty);
		}
	}

	private async Task DisconnectAsync()
	{
		IResponsesSocket? socket;
		lock (_sync)
		{
			socket = _socket;
			_socket = null;
			_pending?.Completion.TrySetException(new IOException("The Responses WebSocket is disconnected."));
		}
		socket?.Dispose();
		if (_reader != null)
		{
			await _reader;
			_reader = null;
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
		Task? active;
		lock (_sync)
		{
			active = _active?.Completion.Task;
		}
		if (active != null)
		{
			await active;
		}
		await _cancelGate.WaitAsync();
		try
		{
			await DisconnectAsync();
		}
		finally
		{
			_cancelGate.Release();
		}
	}
}
