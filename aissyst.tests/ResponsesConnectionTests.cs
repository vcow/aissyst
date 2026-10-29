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
	public async Task InitializationReturnsLeasedContinuationAndNewRequestsUseExplicitParent()
	{
		using var socket = new FakeSocket();
		await using var connection = Create(() => socket);
		var initial = await connection.InitializeSessionAsync("Rules", new Session(), default);
		using var parent = initial.Continuation;
		Assert.Empty(initial.Payload.Summary);
		Assert.False(socket.Sent[0].GetProperty("generate").GetBoolean());
		Assert.Equal("developer", socket.Sent[0].GetProperty("input")[0].GetProperty("role").GetString());
		Assert.Equal(socket.Id(1), socket.Sent[1].GetProperty("previous_response_id").GetString());
		Assert.True(socket.Sent[1].GetProperty("text").GetProperty("format").GetProperty("strict").GetBoolean());
		var first = await connection.SendAsync(new DialogRequest { RequestId = 1 }, parent, default);
		using var firstCursor = first.Continuation;
		var second = await connection.SendAsync(new DialogRequest { RequestId = 2 }, parent, default);
		using var secondCursor = second.Continuation;
		// Receiving first did not silently commit it. The caller explicitly kept the original parent.
		Assert.Equal(socket.Id(2), socket.Sent.Last().GetProperty("previous_response_id").GetString());
		var third = await connection.SendAsync(new DialogRequest { RequestId = 3 }, secondCursor, default);
		third.Continuation.Dispose();
		Assert.Equal(socket.Id(4), socket.Sent.Last().GetProperty("previous_response_id").GetString());
	}

	[Fact]
	public async Task IndependentResponsesCanArriveInReverseOrderWithoutAcceptanceCallbacks()
	{
		using var socket = new FakeSocket();
		await using var connection = Create(() => socket);
		var initial = await connection.InitializeSessionAsync("Rules", new Session(), default);
		using var parent = initial.Continuation;
		socket.Hold = true;
		var first = connection.SendAsync(new DialogRequest { RequestId = 1 }, parent, default);
		var second = connection.SendAsync(new DialogRequest { RequestId = 2 }, parent, default);
		Assert.Equal(2, socket.HeldMessages.Count);
		socket.CompleteHeld(1);
		var b = await second.WaitAsync(Timeout);
		using var cursorB = b.Continuation;
		Assert.Equal(2, b.Payload.RequestId);
		Assert.False(first.IsCompleted);
		socket.CompleteHeld(0);
		var a = await first.WaitAsync(Timeout);
		a.Continuation.Dispose();
		Assert.Equal(1, a.Payload.RequestId);
	}

	[Theory]
	[InlineData("response.failed")]
	[InlineData("response.incomplete")]
	[InlineData("invalid_json")]
	[InlineData("refusal")]
	public async Task RequestErrorsDoNotMutateOrInvalidateConfirmedParent(string failure)
	{
		using var socket = new FakeSocket();
		await using var connection = Create(() => socket);
		var initial = await connection.InitializeSessionAsync("Rules", new Session(), default);
		using var parent = initial.Continuation;
		var lost = 0;
		connection.ConnectionLost += (_, _) => lost++;
		socket.Failure = failure;
		await Assert.ThrowsAnyAsync<Exception>(() => connection.SendAsync(new DialogRequest { RequestId = 1 }, parent, default));
		Assert.Equal(0, lost);
		socket.Failure = null;
		var result = await connection.SendAsync(new DialogRequest { RequestId = 2 }, parent, default);
		result.Continuation.Dispose();
		Assert.Equal(socket.Id(2), socket.Sent.Last().GetProperty("previous_response_id").GetString());
	}

	[Fact]
	public async Task SemanticValidationBelongsToModelRatherThanTransport()
	{
		using var socket = new FakeSocket();
		await using var connection = Create(() => socket);
		var initial = await connection.InitializeSessionAsync("Rules", new Session(), default);
		using var parent = initial.Continuation;
		socket.Failure = "wrong_request_id";
		var result = await connection.SendAsync(new DialogRequest { RequestId = 7 }, parent, default);
		result.Continuation.Dispose();
		Assert.Equal(-1, result.Payload.RequestId);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task DisconnectionIsReportedAndReconnectRequiresExplicitSessionInitialization(bool inFlight)
	{
		using var first = new FakeSocket();
		using var second = new FakeSocket();
		var sockets = new Queue<FakeSocket>([first, second]);
		var tokens = 0;
		await using var connection = new ResponsesConnection("model", _ => { tokens++; return Task.FromResult("test-access-token"); },
			new ChatGPTConnectorOptions(), NullLogger.Instance, () => sockets.Dequeue());
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
		Assert.Equal(2, tokens);
		Assert.False(second.Sent[0].TryGetProperty("previous_response_id", out _));
		await Assert.ThrowsAsync<IOException>(() => connection.SendAsync(new DialogRequest(), oldParent, default));
	}

	[Fact]
	public async Task TimeoutDoesNotReconnectOrPermitReusingAnUnfinishedLane()
	{
		using var socket = new FakeSocket();
		await using var connection = new ResponsesConnection("model", _ => Task.FromResult("test-access-token"),
			new ChatGPTConnectorOptions { ResponseTimeout = TimeSpan.FromMilliseconds(200) }, NullLogger.Instance, () => socket);
		var lost = 0;
		connection.ConnectionLost += (_, _) => lost++;
		var initial = await connection.InitializeSessionAsync("Rules", new Session(), default);
		using var parent = initial.Continuation;
		socket.Hold = true;
		await Assert.ThrowsAsync<TimeoutException>(() => connection.SendAsync(new DialogRequest { RequestId = 1 }, parent, default));
		var heldLane = socket.HeldMessages[0].Item.GetProperty("stream_id").GetString();
		socket.Hold = false;
		var next = await connection.SendAsync(new DialogRequest { RequestId = 2 }, parent, default);
		next.Continuation.Dispose();
		Assert.NotEqual(heldLane, socket.Sent.Last().GetProperty("stream_id").GetString());
		socket.CompleteHeld();
		Assert.Equal(0, lost);
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
	}

	[Fact]
	public async Task ModelAndWireKeepPreviousContinuationWhenNewerResponseFailsValidation()
	{
		using var socket = new FakeSocket();
		await using var connection = Create(() => socket);
		var connector = new WireConnector(connection);
		var selector = new TestSessionSelector();
		var database = new TestSessionDatabase();
		await selector.InitializeAsync();
		await using var processor = new TextProcessor(selector, database, NullLogger<TextProcessor>.Instance);
		var dialog = new DialogCoordinator(processor);
		await using var model = new SessionModel(selector, database, dialog, connector, new ApplicationSettings(), NullLogger<SessionModel>.Instance);
		await model.InitializeAsync();
		await processor.ProcessAsync(new DialogMessage(DateTime.UtcNow, AudioSourceType.Application, "Question"));
		socket.Hold = true;
		var first = model.SendRequestAsync(RequestType.Advice);
		var second = model.SendRequestAsync(RequestType.Reply);
		socket.Failure = "wrong_request_id";
		socket.CompleteHeld(1);
		await Assert.ThrowsAsync<InvalidDataException>(() => second);
		socket.Failure = null;
		socket.CompleteHeld(0);
		Assert.NotNull(await first.WaitAsync(Timeout));
		socket.Hold = false;
		Assert.NotNull(await model.SendRequestAsync(RequestType.Translate));
		Assert.Equal(socket.Id(3), socket.Sent.Last().GetProperty("previous_response_id").GetString());
	}

	private sealed class WireConnector(ResponsesConnection connection) : ILLMConnector
	{
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
		internal string? Failure;
		internal readonly List<(JsonElement Item, int Index)> HeldMessages = [];

		internal string Id(int index) => _prefix + index;
		public Task ConnectAsync(string accessToken, CancellationToken cancellationToken)
		{
			Assert.Equal("test-access-token", accessToken);
			return Task.CompletedTask;
		}
		public Task SendAsync(string message, CancellationToken cancellationToken)
		{
			using var document = JsonDocument.Parse(message);
			var item = document.RootElement.Clone();
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
				throw exception.InnerException ?? new IOException("Socket closed.");
			}
		}
		private void Complete(JsonElement item, int index)
		{
			var id = Id(index);
			if (Failure is "previous_response_not_found" or "unauthorized")
			{
				_incoming.Writer.TryWrite(JsonSerializer.Serialize(new { type = "error", stream_id = item.GetProperty("stream_id").GetString(), status = Failure == "unauthorized" ? 401 : 400,
					error = new { code = Failure } }));
				return;
			}
			if (Failure is "response.failed" or "response.incomplete")
			{
				_incoming.Writer.TryWrite(JsonSerializer.Serialize(new { type = Failure, stream_id = item.GetProperty("stream_id").GetString(), response = new { id } }));
				return;
			}
			_incoming.Writer.TryWrite(JsonSerializer.Serialize(new { type = "response.created", stream_id = item.GetProperty("stream_id").GetString(), response = new { id } }));
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
			_incoming.Writer.TryWrite(JsonSerializer.Serialize(new { type = "response.completed", stream_id = item.GetProperty("stream_id").GetString(),
				response = new { id, status = "completed", output } }));
		}
		public void Dispose() => _incoming.Writer.TryComplete();
	}
}
