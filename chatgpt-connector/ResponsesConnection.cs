using System.Text.Json;
using aissyst.core.LLMConnector;
using aissyst.core.LLMConnector.DTO;
using Microsoft.Extensions.Logging;

namespace chatgpt_connector;

// Transport only: sockets, event correlation, continuation leases, and wire timeouts.
internal sealed class ResponsesConnection : IAsyncDisposable
{
	private sealed class Pending
	{
		internal readonly TaskCompletionSource<JsonElement> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
		internal string? ResponseId;
	}

	private sealed class Continuation(ResponsesConnection owner, IResponsesSocket socket, string responseId, string stream) : ILLMContinuation
	{
		private int _disposed;
		internal readonly ResponsesConnection Owner = owner;
		internal readonly IResponsesSocket Socket = socket;
		internal readonly string ResponseId = responseId;
		internal readonly string Stream = stream;
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
			return Owner.Lease(Socket, ResponseId, Stream);
		}
		public void Dispose()
		{
			if (Interlocked.Exchange(ref _disposed, 1) == 0)
			{
				Owner.ReleaseLease(Socket, Stream);
			}
		}
	}

	private readonly Func<CancellationToken, Task<string>> _getToken;
	private readonly Func<IResponsesSocket> _createSocket;
	private readonly string _model;
	private readonly ChatGPTConnectorOptions _options;
	private readonly ILogger _logger;
	private readonly CancellationTokenSource _shutdown = new();
	private readonly SemaphoreSlim _sendGate = new(1, 1);
	private readonly SemaphoreSlim _requestSlots = new(15, 15);
	private readonly Lock _sync = new();
	private readonly Dictionary<string, Pending> _pending = [];
	private readonly Dictionary<string, int> _leases = [];
	private readonly HashSet<Task> _requests = [];
	private IResponsesSocket? _socket;
	private Task? _reader;
	private Task? _disposal;
	private Exception? _connectionFault;
	public event EventHandler? ConnectionLost;

	internal ResponsesConnection(string model, Func<CancellationToken, Task<string>> getToken,
		ChatGPTConnectorOptions options, ILogger logger, Func<IResponsesSocket>? createSocket = null)
	{
		_model = model;
		_getToken = getToken;
		_options = options;
		_logger = logger;
		_createSocket = createSocket ?? (() => new ResponsesSocket());
	}

	internal async Task ReconnectAsync(CancellationToken cancellationToken)
	{
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
		await _sendGate.WaitAsync(linked.Token);
		try
		{
			lock (_sync)
			{
				ObjectDisposedException.ThrowIf(_disposal != null, this);
				if (_connectionFault is ResponsesApiException { IsAuthorizationError: true })
				{
					throw new UnauthorizedAccessException("ChatGPT authorization failed. Restart the application to sign in again.");
				}
			}
			Disconnect();
			if (_reader != null)
			{
				await _reader;
			}
			await ConnectAsync(linked.Token);
		}
		finally
		{
			_sendGate.Release();
		}
	}

	private async Task ConnectAsync(CancellationToken cancellationToken)
	{
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
		catch (ResponsesApiException exception) when (exception.IsAuthorizationError)
		{
			socket.Dispose();
			throw new UnauthorizedAccessException("ChatGPT authorization failed. Restart the application to sign in again.");
		}
		catch
		{
			socket.Dispose();
			throw;
		}
	}

	internal async Task<LLMResponse> InitializeSessionAsync(string instructions, Session session, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(instructions);
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
		await _sendGate.WaitAsync(linked.Token);
		try
		{
			if (_socket == null || _connectionFault != null)
			{
				if (_connectionFault is ResponsesApiException { IsAuthorizationError: true })
				{
					throw new UnauthorizedAccessException("ChatGPT authorization failed. Restart the application to sign in again.");
				}
				Disconnect();
				if (_reader != null)
				{
					await _reader;
				}
				await ConnectAsync(linked.Token);
			}
			var socket = _socket!;
			var warmup = await ExchangeAsync(socket, instructions, "developer", null, false, "interview", linked.Token);
			var response = await ExchangeAsync(socket, JsonSerializer.Serialize(session), "user",
				warmup.GetProperty("id").GetString(), true, "interview", linked.Token);
			return CreateResult(socket, response, "interview");
		}
		finally
		{
			_sendGate.Release();
		}
	}

	internal Task<LLMResponse> SendAsync(DialogRequest request, ILLMContinuation continuation, CancellationToken cancellationToken)
	{
		if (continuation is not Continuation parent || !ReferenceEquals(parent.Owner, this))
		{
			throw new ArgumentException("The continuation belongs to another connector.", nameof(continuation));
		}
		var snapshot = JsonSerializer.Serialize(request);
		var lease = (Continuation)parent.Retain();
		var operation = SendCoreAsync(snapshot, lease, cancellationToken);
		lock (_sync)
		{
			_requests.Add(operation);
		}
		_ = ObserveAsync(operation);
		return operation;
	}

	private async Task<LLMResponse> SendCoreAsync(string payload, Continuation parent, CancellationToken cancellationToken)
	{
		using (parent)
		using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token))
		{
			if (!await _requestSlots.WaitAsync(_options.ResponseTimeout, linked.Token))
			{
				throw new TimeoutException("All Responses request slots are busy.");
			}
			string? lane = null;
			Pending? pending = null;
			try
			{
				await _sendGate.WaitAsync(linked.Token);
				try
				{
					lock (_sync)
					{
						RequireSocket(parent.Socket);
						lane = Enumerable.Range(0, 31).Select(index => "request_" + index)
							.FirstOrDefault(candidate => !_pending.ContainsKey(candidate) && !_leases.ContainsKey(candidate))
							?? throw new InvalidOperationException("No Responses stream is available.");
						pending = new Pending();
						_pending.Add(lane, pending);
					}
					using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
					timeout.CancelAfter(_options.ResponseTimeout);
					try
					{
						await parent.Socket.SendAsync(ResponsesProtocol.Create(_model, "user", payload, parent.ResponseId, true, lane), timeout.Token);
					}
					catch (Exception exception)
					{
						FailConnection(parent.Socket, exception);
						throw;
					}
				}
				finally
				{
					_sendGate.Release();
				}
				var response = await pending!.Completion.Task.WaitAsync(_options.ResponseTimeout, linked.Token);
				return CreateResult(parent.Socket, response, lane!);
			}
			finally
			{
				if (lane != null && pending != null && !pending.Completion.Task.IsCompleted && !_shutdown.IsCancellationRequested)
				{
					// Retain both lane and parent until the server finishes the timed-out/canceled fork.
					var drain = DrainAsync(lane, pending, parent.Retain());
					lock (_sync)
					{
						_requests.Add(drain);
					}
					_ = ObserveAsync(drain);
				}
				else
				{
					ReleaseRequest(lane, pending);
				}
			}
		}
	}

	private LLMResponse CreateResult(IResponsesSocket socket, JsonElement response, string lane)
	{
		var payload = ResponsesProtocol.Parse(response);
		return new LLMResponse(payload, Lease(socket, response.GetProperty("id").GetString()!, lane));
	}

	private ILLMContinuation Lease(IResponsesSocket socket, string responseId, string lane)
	{
		lock (_sync)
		{
			RequireSocket(socket);
			_leases[lane] = _leases.GetValueOrDefault(lane) + 1;
			return new Continuation(this, socket, responseId, lane);
		}
	}

	private void ReleaseLease(IResponsesSocket socket, string lane)
	{
		lock (_sync)
		{
			if (ReferenceEquals(socket, _socket) && _leases.TryGetValue(lane, out var count))
			{
				if (count == 1)
				{
					_leases.Remove(lane);
				}
				else
				{
					_leases[lane] = count - 1;
				}
			}
		}
	}

	private void RequireSocket(IResponsesSocket socket)
	{
		ObjectDisposedException.ThrowIf(_disposal != null, this);
		if (!ReferenceEquals(socket, _socket) || _connectionFault != null)
		{
			throw new IOException("The Responses continuation belongs to a disconnected socket.");
		}
	}

	private async Task DrainAsync(string lane, Pending pending, ILLMContinuation parent)
	{
		using (parent)
		{
			try { await pending.Completion.Task.WaitAsync(_shutdown.Token); }
			catch (Exception) { /* The caller already received the failure. */ }
			finally { ReleaseRequest(lane, pending); }
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

	private async Task<JsonElement> ExchangeAsync(IResponsesSocket socket, string text, string role, string? previousId,
		bool generate, string lane, CancellationToken cancellationToken)
	{
		var pending = new Pending();
		lock (_sync)
		{
			RequireSocket(socket);
			_pending.Add(lane, pending);
		}
		try
		{
			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeout.CancelAfter(_options.ResponseTimeout);
			await socket.SendAsync(ResponsesProtocol.Create(_model, role, text, previousId, generate, lane), timeout.Token);
			return await pending.Completion.Task.WaitAsync(timeout.Token);
		}
		finally
		{
			lock (_sync)
			{
				if (_pending.TryGetValue(lane, out var current) && ReferenceEquals(current, pending))
				{
					_pending.Remove(lane);
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
					if (!ReferenceEquals(socket, _socket))
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
						if (pending == null || exception.IsAuthorizationError || code == "previous_response_not_found")
						{
							throw exception;
						}
						pending.Completion.TrySetException(exception);
						continue;
					}
					if (pending == null || !item.TryGetProperty("response", out var response))
					{
						continue;
					}
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
			FailConnection(socket, exception);
		}
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
			foreach (var pending in _pending.Values)
			{
				pending.Completion.TrySetException(exception);
			}
			notify = !_shutdown.IsCancellationRequested;
		}
		if (notify)
		{
			_logger.LogWarning("The Responses connection was interrupted ({ErrorType}).", exception.GetType().Name);
			ConnectionLost?.Invoke(this, EventArgs.Empty);
		}
	}

	private async Task ObserveAsync(Task operation)
	{
		try { await operation; }
		catch (Exception) { /* The caller receives the failure. */ }
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
			_leases.Clear();
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
		await _sendGate.WaitAsync();
		_sendGate.Release();
		if (_reader != null)
		{
			await _reader;
		}
		Task[] requests;
		lock (_sync)
		{
			requests = _requests.ToArray();
		}
		try { await Task.WhenAll(requests); }
		catch (Exception) { /* Individual requests have already completed their callers. */ }
	}
}
