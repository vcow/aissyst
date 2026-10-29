using System.Text.Json;
using System.Threading.Channels;
using aissyst.core.LLMConnector;
using aissyst.core.LLMConnector.Model;
using aissyst.core.Processor;
using aissyst.core;
using aissyst.console;
using aissyst.core.LLMConnector.DTO;
using chatgpt_connector;
using Microsoft.Extensions.Logging.Abstractions;

namespace aissyst.tests;

public sealed class ResponsesConnectionTests
{
	private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

	[Fact]
	public async Task InitializationAndRequestsUseExplicitParentWithoutNamedStreams()
	{
		using var socket = new FakeSocket();
		await using var connection = Create(() => socket);
		Assert.False(connection.IsBusy);
		await connection.CancelAsync();
		var initial = await connection.InitializeSessionAsync("Rules", new Session(), default);
		using var parent = initial.Continuation;
		Assert.False(connection.IsBusy);
		Assert.Empty(initial.Payload.Summary);
		Assert.False(socket.Sent[0].GetProperty("generate").GetBoolean());
		Assert.Equal("developer", socket.Sent[0].GetProperty("input")[0].GetProperty("role").GetString());
		Assert.Equal(socket.Id(1), socket.Sent[1].GetProperty("previous_response_id").GetString());
		Assert.True(socket.Sent[1].GetProperty("text").GetProperty("format").GetProperty("strict").GetBoolean());
		var first = await connection.SendAsync(new DialogRequest { RequestId = 1 }, parent, default);
		using var nextParent = first.Continuation;
		var second = await connection.SendAsync(new DialogRequest { RequestId = 2 }, nextParent, default);
		second.Continuation.Dispose();
		Assert.Equal(socket.Id(3), socket.Sent.Last().GetProperty("previous_response_id").GetString());
		await connection.CancelAsync();
		Assert.Equal(0, socket.Disposals);
		Assert.All(socket.Sent, item => Assert.False(item.TryGetProperty("stream_id", out _)));
	}

	[Fact]
	public async Task BusyRequestsFailImmediatelyWithoutQueueingOrCancelingTheActiveRequest()
	{
		using var socket = new FakeSocket();
		await using var connection = Create(() => socket);
		var initial = await connection.InitializeSessionAsync("Rules", new Session(), default);
		using var parent = initial.Continuation;
		socket.Hold = true;
		var first = connection.SendAsync(new DialogRequest { RequestId = 1 }, parent, default);
		Assert.True(connection.IsBusy);
		await Assert.ThrowsAsync<InvalidOperationException>(() => connection.SendAsync(new DialogRequest { RequestId = 2 }, parent, default));
		await Assert.ThrowsAsync<InvalidOperationException>(() => connection.InitializeSessionAsync("Rules", new Session(), default));
		Assert.Single(socket.HeldMessages);
		Assert.False(first.IsCompleted);
		socket.CompleteHeld();
		var result = await first.WaitAsync(Timeout);
		using var nextParent = result.Continuation;
		Assert.False(connection.IsBusy);
		socket.Hold = false;
		var next = await connection.SendAsync(new DialogRequest { RequestId = 3 }, nextParent, default);
		next.Continuation.Dispose();
		Assert.Equal(4, socket.Sent.Count);
	}

	[Theory]
	[InlineData("response.failed")]
	[InlineData("response.incomplete")]
	[InlineData("invalid_json")]
	[InlineData("refusal")]
	public async Task CompletedRequestErrorsReleaseBusyStateWithoutChangingTheCallerParent(string failure)
	{
		using var socket = new FakeSocket();
		await using var connection = Create(() => socket);
		var initial = await connection.InitializeSessionAsync("Rules", new Session(), default);
		using var parent = initial.Continuation;
		var lost = 0;
		connection.ConnectionLost += (_, _) => lost++;
		socket.Failure = failure;
		await Assert.ThrowsAnyAsync<Exception>(() => connection.SendAsync(new DialogRequest { RequestId = 1 }, parent, default));
		Assert.False(connection.IsBusy);
		Assert.Equal(0, lost);
		socket.Failure = null;
		var result = await connection.SendAsync(new DialogRequest { RequestId = 2 }, parent, default);
		result.Continuation.Dispose();
		Assert.Equal(socket.Id(2), socket.Sent.Last().GetProperty("previous_response_id").GetString());
	}

	[Fact]
	public async Task SimultaneousSendAttemptsAdmitOnlyOneRequest()
	{
		using var socket = new FakeSocket();
		await using var connection = Create(() => socket);
		var initial = await connection.InitializeSessionAsync("Rules", new Session(), default);
		using var parent = initial.Continuation;
		socket.Hold = true;
		using var start = new ManualResetEventSlim();
		var attempts = Enumerable.Range(0, 2).Select(index => Task.Run(() =>
		{
			start.Wait();
			try
			{
				return (Request: connection.SendAsync(new DialogRequest { RequestId = index + 1 }, parent, default), Error: (Exception?)null);
			}
			catch (Exception exception)
			{
				return (Request: (Task<LLMResponse>?)null, Error: exception);
			}
		})).ToArray();
		start.Set();
		var results = await Task.WhenAll(attempts).WaitAsync(Timeout);
		var admitted = Assert.Single(results, item => item.Request != null).Request!;
		Assert.IsType<InvalidOperationException>(Assert.Single(results, item => item.Error != null).Error);
		Assert.Single(socket.HeldMessages);
		socket.CompleteHeld();
		var result = await admitted.WaitAsync(Timeout);
		result.Continuation.Dispose();
		Assert.False(connection.IsBusy);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task DisconnectionIsReportedAndReconnectRequiresExplicitSessionInitialization(bool inFlight)
	{
		using var first = new FakeSocket();
		using var second = new FakeSocket();
		var sockets = new Queue<FakeSocket>([first, second]);
		await using var connection = Create(() => sockets.Dequeue());
		var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		connection.ConnectionLost += (_, _) => lost.TrySetResult();
		var initial = await connection.InitializeSessionAsync("Rules", new Session(), default);
		using var oldParent = initial.Continuation;
		Task<LLMResponse>? pending = null;
		if (inFlight)
		{
			first.Hold = true;
			pending = connection.SendAsync(new DialogRequest { RequestId = 1 }, oldParent, default);
		}
		first.Fail(new IOException("Disconnected"));
		await lost.Task.WaitAsync(Timeout);
		Assert.False(oldParent.IsValid);
		if (pending != null)
		{
			await Assert.ThrowsAsync<IOException>(() => pending);
		}
		await connection.ReconnectAsync(default);
		Assert.Empty(second.Sent);
		var restored = await connection.InitializeSessionAsync("Rules", new Session(), default);
		restored.Continuation.Dispose();
		Assert.False(second.Sent[0].TryGetProperty("previous_response_id", out _));
		await Assert.ThrowsAsync<IOException>(() => connection.SendAsync(new DialogRequest(), oldParent, default));
		Assert.False(connection.IsBusy);
	}

	[Fact]
	public async Task CancelClosesTheSocketOnceAndDiscardsConcurrentCancellationCalls()
	{
		using var socket = new FakeSocket();
		await using var connection = Create(() => socket);
		var initial = await connection.InitializeSessionAsync("Rules", new Session(), default);
		using var parent = initial.Continuation;
		var readerRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		socket.ReceiveCloseWait = readerRelease.Task;
		socket.Hold = true;
		var lost = 0;
		connection.ConnectionLost += (_, _) => Interlocked.Increment(ref lost);
		var request = connection.SendAsync(new DialogRequest { RequestId = 1 }, parent, default);
		var cancellation = connection.CancelAsync();
		try
		{
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
			Assert.True(connection.IsBusy);
			Assert.False(cancellation.IsCompleted);
			await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => connection.CancelAsync())).WaitAsync(Timeout);
			await Assert.ThrowsAsync<InvalidOperationException>(() => connection.SendAsync(new DialogRequest(), parent, default));
			Assert.Equal(1, socket.Disposals);
			Assert.Equal(1, lost);
		}
		finally
		{
			readerRelease.TrySetResult();
			await cancellation.WaitAsync(Timeout);
		}
		Assert.False(connection.IsBusy);
		Assert.False(parent.IsValid);
		await connection.CancelAsync();
		Assert.Equal(1, lost);
	}

	[Fact]
	public async Task CancellationDuringConnectionSetupClosesTheConnectingSocket()
	{
		var connecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var socket = new FakeSocket { ConnectWait = connecting.Task };
		await using var connection = Create(() => socket);
		var initialization = connection.InitializeSessionAsync("Rules", new Session(), default);
		Assert.True(connection.IsBusy);
		await connection.CancelAsync().WaitAsync(Timeout);
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialization);
		Assert.False(connection.IsBusy);
		Assert.Equal(1, socket.Disposals);
		Assert.Empty(socket.Sent);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task TimeoutOrCallerCancellationClosesUnfinishedGenerationBeforeReleasingBusy(bool callerCancellation)
	{
		using var socket = new FakeSocket();
		await using var connection = new ResponsesConnection("model", _ => Task.FromResult("test-access-token"),
			new ChatGPTConnectorOptions { ResponseTimeout = TimeSpan.FromMilliseconds(200) }, NullLogger.Instance, () => socket);
		var initial = await connection.InitializeSessionAsync("Rules", new Session(), default);
		using var parent = initial.Continuation;
		socket.Hold = true;
		var lost = 0;
		connection.ConnectionLost += (_, _) => Interlocked.Increment(ref lost);
		using var cancellation = new CancellationTokenSource();
		var request = connection.SendAsync(new DialogRequest { RequestId = 1 }, parent, cancellation.Token);
		Assert.True(connection.IsBusy);
		if (callerCancellation)
		{
			await cancellation.CancelAsync();
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
		}
		else
		{
			await Assert.ThrowsAsync<TimeoutException>(() => request);
		}
		Assert.False(connection.IsBusy);
		Assert.False(parent.IsValid);
		Assert.Equal(1, socket.Disposals);
		Assert.Equal(1, lost);
	}

	[Fact]
	public async Task DisposalCancelsPendingWireResponse()
	{
		using var socket = new FakeSocket();
		var connection = Create(() => socket);
		var initial = await connection.InitializeSessionAsync("Rules", new Session(), default);
		using var parent = initial.Continuation;
		socket.Hold = true;
		var pending = connection.SendAsync(new DialogRequest { RequestId = 1 }, parent, default);
		await connection.DisposeAsync().AsTask().WaitAsync(Timeout);
		await Assert.ThrowsAnyAsync<Exception>(() => pending);
		Assert.False(connection.IsBusy);
		await connection.DisposeAsync();
	}

	[Fact]
	public async Task EmergencyCancellationRestoresModelAndAllowsANewRequestOnTheNewSocket()
	{
		using var first = new FakeSocket();
		using var second = new FakeSocket();
		var sockets = new Queue<FakeSocket>([first, second]);
		await using var connection = Create(() => sockets.Dequeue());
		var connector = new WireConnector(connection);
		var selector = new TestSessionSelector();
		var database = new TestSessionDatabase();
		await selector.InitializeAsync();
		await using var processor = new TextProcessor(selector, database, NullLogger<TextProcessor>.Instance);
		var dialog = new DialogCoordinator(processor);
		await using var model = new SessionModel(selector, database, dialog, connector, new ApplicationSettings(),
			NullLogger<SessionModel>.Instance, new SessionRecoveryOptions { Delay = TimeSpan.Zero });
		await model.InitializeAsync();
		await processor.ProcessAsync(new DialogMessage(DateTime.UtcNow, AudioSourceType.Application, "Question"));
		first.Hold = true;
		var pending = model.SendRequestAsync(RequestType.Advice);
		await connector.CancelAsync().WaitAsync(Timeout);
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
		await SessionModelTests.WaitUntilAsync(() => model.IsReady && second.Sent.Count == 2);
		Assert.False(connector.IsBusy);
		Assert.False(second.Sent[0].TryGetProperty("previous_response_id", out _));
		first.CompleteHeld();
		Assert.NotNull(await model.SendRequestAsync(RequestType.Translate));
		Assert.Equal(second.Id(2), second.Sent.Last().GetProperty("previous_response_id").GetString());
		Assert.Empty(sockets);
	}

	private sealed class WireConnector(ResponsesConnection connection) : ILLMConnector
	{
		public bool IsBusy => connection.IsBusy;
		public Task CancelAsync(CancellationToken cancellationToken = default) => connection.CancelAsync(cancellationToken);
		public event EventHandler? ConnectionLost
		{
			add => connection.ConnectionLost += value;
			remove => connection.ConnectionLost -= value;
		}
		public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
		public Task ReconnectAsync(CancellationToken cancellationToken = default) => connection.ReconnectAsync(cancellationToken);
		public Task<LLMResponse> InitializeSession(string instructions, Session session, CancellationToken cancellationToken = default) =>
			connection.InitializeSessionAsync(instructions, session, cancellationToken);
		public Task<LLMResponse> SendRequest(DialogRequest request, ILLMContinuation continuation, CancellationToken cancellationToken = default) =>
			connection.SendAsync(request, continuation, cancellationToken);
	}

	private static ResponsesConnection Create(Func<IResponsesSocket> factory) => new("model",
		_ => Task.FromResult("test-access-token"), new ChatGPTConnectorOptions(), NullLogger.Instance, factory);

	internal sealed class FakeSocket : IResponsesSocket
	{
		private readonly Channel<string> _incoming = Channel.CreateUnbounded<string>();
		private readonly string _prefix = Guid.NewGuid().ToString("N");
		internal readonly List<JsonElement> Sent = [];
		internal readonly TaskCompletionSource Held = new(TaskCreationOptions.RunContinuationsAsynchronously);
		internal bool Hold;
		internal Task? ConnectWait;
		internal Task? ReceiveCloseWait;
		private int _disposed;
		internal int Disposals;
		internal string? Failure;
		internal Func<JsonElement, object>? ErrorEvent;
		internal readonly List<(JsonElement Item, int Index)> HeldMessages = [];

		internal string Id(int index) => _prefix + index;
		public async Task ConnectAsync(string accessToken, CancellationToken cancellationToken)
		{
			Assert.Equal("test-access-token", accessToken);
			if (ConnectWait != null)
			{
				await ConnectWait.WaitAsync(cancellationToken);
			}
		}
		public Task SendAsync(string message, CancellationToken cancellationToken)
		{
			using var document = JsonDocument.Parse(message);
			var item = document.RootElement.Clone();
			Assert.False(item.TryGetProperty("stream_id", out _));
			Sent.Add(item);
			if (Hold)
			{
				HeldMessages.Add((item, Sent.Count));
				Held.TrySetResult();
			}
			else
			{
				Complete(item, Sent.Count);
			}
			return Task.CompletedTask;
		}
		internal void CompleteHeld(int index = 0) => Complete(HeldMessages[index].Item, HeldMessages[index].Index);
		internal void Fail(Exception exception) => _incoming.Writer.TryComplete(exception);
		public async Task<string> ReceiveAsync(CancellationToken cancellationToken)
		{
			try
			{
				return await _incoming.Reader.ReadAsync(cancellationToken);
			}
			catch (ChannelClosedException exception)
			{
				if (ReceiveCloseWait != null)
				{
					await ReceiveCloseWait;
				}
				throw exception.InnerException ?? new IOException("Socket closed.");
			}
		}
		private void Complete(JsonElement item, int index)
		{
			var id = Id(index);
			if (ErrorEvent != null)
			{
				_incoming.Writer.TryWrite(JsonSerializer.Serialize(ErrorEvent(item)));
				return;
			}
			if (Failure is "previous_response_not_found" or "unauthorized")
			{
				_incoming.Writer.TryWrite(JsonSerializer.Serialize(new { type = "error", status = Failure == "unauthorized" ? 401 : 400,
					error = new { code = Failure } }));
				return;
			}
			if (Failure is "response.failed" or "response.incomplete")
			{
				_incoming.Writer.TryWrite(JsonSerializer.Serialize(new { type = Failure, response = new { id } }));
				return;
			}
			_incoming.Writer.TryWrite(JsonSerializer.Serialize(new { type = "response.created", response = new { id } }));
			object[] output = [];
			if (!item.TryGetProperty("generate", out _))
			{
				using var payload = JsonDocument.Parse(item.GetProperty("input")[0].GetProperty("content")[0].GetProperty("text").GetString()!);
				var root = payload.RootElement;
				var initialization = root.TryGetProperty("dialog", out var records);
				if (!initialization)
				{
					records = root.GetProperty("raw_records");
				}
				var timestamp = records.GetArrayLength() > 0 ? records[records.GetArrayLength() - 1].GetProperty("ts").GetDateTime() : (DateTime?)null;
				var summary = timestamp.HasValue ? "Updated summary" : initialization ? "" : root.GetProperty("summary").GetString();
				var dto = new { request_id = Failure == "wrong_request_id" ? -1 : root.GetProperty("request_id").GetInt32(),
					error = Failure == "model_error" ? "Model error" : "", summary,
					summary_ts = Failure == "wrong_timestamp" ? DateTime.UtcNow : timestamp, message = initialization ? "" : "Answer" };
				object content = Failure == "refusal" ? new { type = "refusal", refusal = "Refused" } :
					new { type = "output_text", text = Failure == "invalid_json" ? "{invalid" : JsonSerializer.Serialize(dto) };
				output = [new { type = "message", content = new[] { content } }];
			}
			_incoming.Writer.TryWrite(JsonSerializer.Serialize(new { type = "response.completed",
				response = new { id, status = "completed", output } }));
		}
		public void Dispose()
		{
			if (Interlocked.Exchange(ref _disposed, 1) == 0)
			{
				Disposals++;
				_incoming.Writer.TryComplete();
			}
		}
	}
}
