using System.Text.Json;
using System.Threading.Channels;
using aissyst.core.LLMConnector.DTO;
using chatgpt_connector;
using Microsoft.Extensions.Logging.Abstractions;

namespace aissyst.tests;

public sealed class ResponsesConnectionTests
{
	private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

	[Fact]
	public async Task WarmupAndSessionAreSequentialAndRequestsUseConfirmedResponse()
	{
		using var socket = new FakeSocket();
		await using var connection = Create(() => socket);
		var accepted = new List<DialogResponse>();
		connection.Configure("Developer instructions", () => new Session(), (_, _) => { }, response =>
		{
			accepted.Add(response);
			return true;
		});
		var initial = await connection.InitializeSessionAsync(new Session(), default);
		Assert.Empty(initial.Summary);
		var sent = socket.Sent.ToArray();
		Assert.Equal(2, sent.Length);
		Assert.False(sent[0].GetProperty("generate").GetBoolean());
		Assert.False(sent[0].GetProperty("store").GetBoolean());
		Assert.False(sent[0].TryGetProperty("stream", out _));
		Assert.False(sent[0].TryGetProperty("previous_response_id", out _));
		Assert.Equal("developer", sent[0].GetProperty("input")[0].GetProperty("role").GetString());
		Assert.Equal("Developer instructions", sent[0].GetProperty("input")[0].GetProperty("content")[0].GetProperty("text").GetString());
		Assert.Equal(socket.Id(1), sent[1].GetProperty("previous_response_id").GetString());
		Assert.True(sent[1].GetProperty("text").GetProperty("format").GetProperty("strict").GetBoolean());
		var timestamp = DateTime.UtcNow;
		var result = await connection.SendAsync(new DialogRequest
		{
			RequestId = 7, Request = RequestType.Reply,
			RawRecords = [new DialogItem { Timestamp = timestamp, Message = "Question" }]
		}, default);
		Assert.Equal(timestamp, result.SummaryTimestamp);
		Assert.Equal("Answer", result.Message);
		Assert.Single(accepted);
		Assert.Equal(socket.Id(2), socket.Sent.Last().GetProperty("previous_response_id").GetString());
		await connection.SendAsync(new DialogRequest { RequestId = 8, Summary = result.Summary }, default);
		Assert.Equal(socket.Id(3), socket.Sent.Last().GetProperty("previous_response_id").GetString());
	}

	[Fact]
	public async Task DisconnectFailsInFlightRequestAndRestoresFreshSessionWithoutReplayingRequest()
	{
		using var first = new FakeSocket();
		using var second = new FakeSocket();
		var sockets = new Queue<FakeSocket>([first, second]);
		await using var connection = Create(() => sockets.Dequeue());
		var timestamp = DateTime.UtcNow;
		var session = new Session();
		var recovered = new TaskCompletionSource<DialogResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
		connection.Configure("Rules", () => session, (_, response) => recovered.SetResult(response), _ => true);
		await connection.InitializeSessionAsync(session, default);
		first.Hold = true;
		var request = connection.SendAsync(new DialogRequest { RequestId = 1 }, default);
		await first.Held.Task.WaitAsync(Timeout);
		session = new Session { Dialog = [new DialogItem { Timestamp = timestamp, Message = "New real dialog" }] };
		first.Fail(new IOException("Disconnected"));
		await Assert.ThrowsAsync<IOException>(() => request);
		var restored = await recovered.Task.WaitAsync(Timeout);
		Assert.Equal(timestamp, restored.SummaryTimestamp);
		Assert.Equal(2, second.Sent.Count);
		Assert.False(second.Sent.First().TryGetProperty("previous_response_id", out _));
		var payload = JsonDocument.Parse(second.Sent.Last().GetProperty("input")[0].GetProperty("content")[0].GetProperty("text").GetString()!);
		Assert.Equal("New real dialog", payload.RootElement.GetProperty("dialog")[0].GetProperty("message").GetString());
		Assert.False(payload.RootElement.TryGetProperty("request", out _));
	}

	[Theory]
	[InlineData("previous_response_not_found")]
	[InlineData("response.failed")]
	[InlineData("response.incomplete")]
	[InlineData("invalid_json")]
	[InlineData("refusal")]
	[InlineData("wrong_request_id")]
	[InlineData("wrong_timestamp")]
	public async Task FailedResponseIsNotAppliedAndRecoveryDoesNotRetryIt(string failure)
	{
		using var first = new FakeSocket();
		using var second = new FakeSocket();
		var sockets = new Queue<FakeSocket>([first, second]);
		await using var connection = Create(() => sockets.Dequeue());
		var applied = 0;
		var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		connection.Configure("Rules", () => new Session(), (_, _) => recovered.SetResult(), _ => { applied++; return true; });
		await connection.InitializeSessionAsync(new Session(), default);
		first.Failure = failure;
		await Assert.ThrowsAnyAsync<Exception>(() => connection.SendAsync(new DialogRequest { RequestId = 9 }, default));
		await recovered.Task.WaitAsync(Timeout);
		Assert.Equal(0, applied);
		Assert.Equal(2, second.Sent.Count);
	}

	[Fact]
	public async Task ModelErrorKeepsConfirmedResponseAndDoesNotApplySummary()
	{
		using var socket = new FakeSocket();
		await using var connection = Create(() => socket);
		var applied = 0;
		connection.Configure("Rules", () => new Session(), (_, _) => { }, _ => { applied++; return true; });
		await connection.InitializeSessionAsync(new Session(), default);
		socket.Failure = "model_error";
		var result = await connection.SendAsync(new DialogRequest { RequestId = 1 }, default);
		Assert.NotEmpty(result.Error);
		Assert.Equal(0, applied);
		socket.Failure = null;
		await connection.SendAsync(new DialogRequest { RequestId = 2 }, default);
		Assert.Equal(socket.Id(2), socket.Sent.Last().GetProperty("previous_response_id").GetString());
	}

	[Fact]
	public async Task CancellationReturnsPromptlyAndLateResultNeverApplies()
	{
		using var first = new FakeSocket();
		using var second = new FakeSocket();
		var sockets = new Queue<FakeSocket>([first, second]);
		await using var connection = Create(() => sockets.Dequeue());
		var applied = 0;
		var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		connection.Configure("Rules", () => new Session(), (_, _) => recovered.SetResult(), _ => { applied++; return true; });
		await connection.InitializeSessionAsync(new Session(), default);
		first.Hold = true;
		using var cancellation = new CancellationTokenSource();
		var pending = connection.SendAsync(new DialogRequest { RequestId = 1 }, cancellation.Token);
		await first.Held.Task.WaitAsync(Timeout);
		await cancellation.CancelAsync();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Timeout));
		first.CompleteHeld();
		await recovered.Task.WaitAsync(Timeout);
		Assert.Equal(0, applied);
	}

	[Fact]
	public async Task DisposalCancelsPendingResponseAndDoesNotReconnect()
	{
		using var socket = new FakeSocket();
		var count = 0;
		var connection = Create(() => { count++; return socket; });
		connection.Configure("Rules", () => new Session(), (_, _) => { }, _ => true);
		await connection.InitializeSessionAsync(new Session(), default);
		socket.Hold = true;
		var pending = connection.SendAsync(new DialogRequest { RequestId = 1 }, default);
		await socket.Held.Task.WaitAsync(Timeout);
		await connection.DisposeAsync().AsTask().WaitAsync(Timeout);
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
		Assert.Equal(1, count);
	}

	[Fact]
	public async Task IdleDisconnectAlsoRestoresConnection()
	{
		using var first = new FakeSocket();
		using var second = new FakeSocket();
		var sockets = new Queue<FakeSocket>([first, second]);
		await using var connection = Create(() => sockets.Dequeue());
		var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		connection.Configure("Rules", () => new Session(), (_, _) => recovered.SetResult(), _ => true);
		await connection.InitializeSessionAsync(new Session(), default);
		first.Fail(new IOException("Idle disconnect"));
		await recovered.Task.WaitAsync(Timeout);
		Assert.Equal(2, second.Sent.Count);
	}

	[Fact]
	public async Task ResponseTimeoutRecoversWithoutRetryingTheTimedOutRequest()
	{
		using var first = new FakeSocket();
		using var second = new FakeSocket();
		var sockets = new Queue<FakeSocket>([first, second]);
		await using var connection = new ResponsesConnection("model", _ => Task.FromResult("test-access-token"),
			new ChatGPTConnectorOptions { ResponseTimeout = TimeSpan.FromMilliseconds(200), ReconnectDelay = TimeSpan.Zero },
			NullLogger.Instance, () => sockets.Dequeue());
		var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		connection.Configure("Rules", () => new Session(), (_, _) => recovered.SetResult(), _ => true);
		await connection.InitializeSessionAsync(new Session(), default);
		first.Hold = true;
		await Assert.ThrowsAsync<TimeoutException>(() => connection.SendAsync(new DialogRequest { RequestId = 1 }, default));
		await recovered.Task.WaitAsync(Timeout);
		Assert.Equal(2, second.Sent.Count);
	}

	[Fact]
	public async Task AuthorizationErrorDoesNotCauseRepeatedReconnects()
	{
		using var socket = new FakeSocket();
		var created = 0;
		await using var connection = Create(() => { created++; return socket; });
		connection.Configure("Rules", () => new Session(), (_, _) => { }, _ => true);
		await connection.InitializeSessionAsync(new Session(), default);
		socket.Failure = "unauthorized";
		await Assert.ThrowsAsync<ResponsesApiException>(() => connection.SendAsync(new DialogRequest { RequestId = 1 }, default));
		Assert.Equal(1, created);
	}

	[Fact]
	public async Task ConnectionLifetimeStartsNewChainAndRefreshesAccessToken()
	{
		using var first = new FakeSocket();
		using var second = new FakeSocket();
		var tokens = 0;
		var sockets = new Queue<FakeSocket>([first, second]);
		await using var connection = new ResponsesConnection("model", _ =>
		{
			tokens++;
			return Task.FromResult("test-access-token");
		}, new ChatGPTConnectorOptions { ConnectionLifetime = TimeSpan.FromMilliseconds(100), ReconnectDelay = TimeSpan.Zero },
			NullLogger.Instance, () => sockets.Dequeue());
		var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		connection.Configure("Rules", () => new Session(), (_, _) => recovered.TrySetResult(), _ => true);
		await connection.InitializeSessionAsync(new Session(), default);
		await recovered.Task.WaitAsync(Timeout);
		Assert.Equal(2, tokens);
		Assert.False(second.Sent.First().TryGetProperty("previous_response_id", out _));
	}

	private static ResponsesConnection Create(Func<IResponsesSocket> factory) => new("selected-model",
		_ => Task.FromResult("test-access-token"), new ChatGPTConnectorOptions { ReconnectDelay = TimeSpan.Zero },
		NullLogger.Instance, factory);

	internal sealed class FakeSocket : IResponsesSocket
	{
		private readonly Channel<string> _incoming = Channel.CreateUnbounded<string>();
		private readonly string _prefix = Guid.NewGuid().ToString("N");
		internal readonly List<JsonElement> Sent = [];
		internal readonly TaskCompletionSource Held = new(TaskCreationOptions.RunContinuationsAsynchronously);
		internal bool Hold;
		internal string? Failure;
		private JsonElement _held;
		private int _heldIndex;

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
				_held = item;
				_heldIndex = Sent.Count;
				Held.TrySetResult();
			}
			else
			{
				Complete(item, Sent.Count);
			}
			return Task.CompletedTask;
		}
		internal void CompleteHeld() => Complete(_held, _heldIndex);
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
				_incoming.Writer.TryWrite(JsonSerializer.Serialize(new { type = "error", stream_id = "interview", status = Failure == "unauthorized" ? 401 : 400,
					error = new { code = Failure } }));
				return;
			}
			if (Failure is "response.failed" or "response.incomplete")
			{
				_incoming.Writer.TryWrite(JsonSerializer.Serialize(new { type = Failure, stream_id = "interview", response = new { id } }));
				return;
			}
			_incoming.Writer.TryWrite(JsonSerializer.Serialize(new { type = "response.created", stream_id = "interview", response = new { id } }));
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
			_incoming.Writer.TryWrite(JsonSerializer.Serialize(new { type = "response.completed", stream_id = "interview",
				response = new { id, status = "completed", output } }));
		}
		public void Dispose() => _incoming.Writer.TryComplete();
	}
}
