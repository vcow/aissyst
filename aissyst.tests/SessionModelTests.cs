using System.Collections.Concurrent;
using System.Threading.Channels;
using aissyst.core;
using aissyst.console;
using aissyst.core.Database;
using aissyst.core.LLMConnector.DTO;
using aissyst.core.LLMConnector.Model;
using aissyst.core.Processor;
using Microsoft.Extensions.Logging.Abstractions;

namespace aissyst.tests;

public sealed class SessionModelTests
{
	[Fact]
	public async Task InitializationLoadsInstructionsAndRestoredDialogAndUsesOneSnapshot()
	{
		await using var fixture = new Fixture();
		var timestamp = DateTime.UtcNow;
		fixture.Database.Session = fixture.Database.Session with { Dialog = [new DialogRecord(timestamp, "interviewer", "Stored")] };
		await fixture.InitializeAsync();
		Assert.Equal("Test initial instructions", fixture.Connector.Instructions);
		Assert.Equal(1, fixture.Selector.Calls);
		Assert.Equal("Stored", Assert.Single(fixture.Model.GetSession().Dialog).Message);
		DialogRequest? sent = null;
		fixture.Connector.Send = (request, _) => { sent = request; return Task.FromResult(Answer(request)); };
		await fixture.Model.SendRequestAsync(RequestType.Reply);
		Assert.Empty(sent!.RawRecords);
		Assert.Equal("Restored", sent.Summary);
		await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Model.InitializeAsync());
		var snapshot = fixture.Model.GetSession();
		snapshot.Context[0].Body = "Changed";
		Assert.Equal("Test context", fixture.Model.GetSession().Context[0].Body);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task RequestOrderUsesLastAcceptedResponseAndMatchingContinuation(bool newerFirst)
	{
		await using var fixture = new Fixture();
		await fixture.InitializeAsync();
		await fixture.Processor.ProcessAsync(new DialogMessage(DateTime.UtcNow, AudioSourceType.Application, "Question"));
		var sent = Channel.CreateUnbounded<DialogRequest>();
		var completions = new ConcurrentDictionary<int, TaskCompletionSource<DialogResponse>>();
		fixture.Connector.Send = async (request, _) =>
		{
			var completion = new TaskCompletionSource<DialogResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
			completions[request.RequestId] = completion;
			await sent.Writer.WriteAsync(request);
			return await completion.Task;
		};
		var a = fixture.Model.SendRequestAsync(RequestType.Advice);
		var requestA = await sent.Reader.ReadAsync();
		var b = fixture.Model.SendRequestAsync(RequestType.Reply);
		var requestB = await sent.Reader.ReadAsync();
		Assert.True(requestB.RequestId > requestA.RequestId);
		if (newerFirst)
		{
			completions[requestB.RequestId].SetResult(Answer(requestB, "B"));
			Assert.Equal("B", (await b)!.Message);
			completions[requestA.RequestId].SetResult(Answer(requestA, "A"));
			Assert.Null(await a);
		}
		else
		{
			completions[requestA.RequestId].SetResult(Answer(requestA, "A"));
			Assert.Equal("A", (await a)!.Message);
			Assert.False(b.IsCompleted);
			completions[requestB.RequestId].SetResult(Answer(requestB, "B"));
			Assert.Equal("B", (await b)!.Message);
		}
		fixture.Connector.Send = (request, _) => Task.FromResult(Answer(request));
		await fixture.Model.SendRequestAsync(RequestType.Translate);
		Assert.Equal(requestB.RequestId.ToString(), ((TestContinuation)fixture.Connector.Parents.Last()).Id);
	}

	[Theory]
	[InlineData("request_id")]
	[InlineData("timestamp")]
	[InlineData("summary")]
	[InlineData("error")]
	public async Task InvalidResponseDoesNotAdvanceSummaryBoundaryOrContinuation(string fault)
	{
		await using var fixture = new Fixture();
		await fixture.InitializeAsync();
		await fixture.Processor.ProcessAsync(new DialogMessage(DateTime.UtcNow, AudioSourceType.Application, "Question"));
		fixture.Connector.Send = (request, _) =>
		{
			var response = Answer(request);
			switch (fault)
			{
				case "request_id": response.RequestId++; break;
				case "timestamp": response.SummaryTimestamp = DateTime.UtcNow.AddDays(1); break;
				case "summary": response.Summary = ""; break;
				case "error": response.Error = "Failure"; break;
			}
			return Task.FromResult(response);
		};
		await Assert.ThrowsAnyAsync<Exception>(() => fixture.Model.SendRequestAsync(RequestType.Reply));
		fixture.Connector.Send = (request, _) =>
		{
			Assert.Equal("", request.Summary);
			Assert.Equal("Question", Assert.Single(request.RawRecords).Message);
			return Task.FromResult(Answer(request));
		};
		await fixture.Model.SendRequestAsync(RequestType.Reply);
		Assert.All(fixture.Connector.Parents, parent => Assert.Equal("session", ((TestContinuation)parent).Id));
	}

	[Fact]
	public async Task LaterErrorDoesNotPreventEarlierValidResponse()
	{
		await using var fixture = new Fixture();
		await fixture.InitializeAsync();
		var first = new TaskCompletionSource<DialogRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource<DialogResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
		fixture.Connector.Send = (request, _) =>
		{
			if (request.Request == RequestType.Advice)
			{
				first.SetResult(request);
				return release.Task;
			}
			return Task.FromResult(new DialogResponse { RequestId = request.RequestId, Error = "B failed" });
		};
		var a = fixture.Model.SendRequestAsync(RequestType.Advice);
		var sentA = await first.Task;
		await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Model.SendRequestAsync(RequestType.Reply));
		release.SetResult(Answer(sentA));
		Assert.NotNull(await a);
	}

	[Fact]
	public async Task ResponseBoundaryUsesSentSnapshotAndLaterFragmentsRemainRaw()
	{
		await using var fixture = new Fixture();
		await fixture.InitializeAsync();
		var timestamp = DateTime.UtcNow;
		await fixture.Processor.ProcessAsync(new DialogMessage(timestamp, AudioSourceType.Application, "First"));
		fixture.Connector.Send = async (request, _) =>
		{
			await fixture.Processor.ProcessAsync(new DialogMessage(timestamp.AddSeconds(1), AudioSourceType.Application, "Later"));
			Assert.Equal("First", Assert.Single(request.RawRecords).Message);
			return Answer(request);
		};
		await fixture.Model.SendRequestAsync(RequestType.Reply);
		fixture.Connector.Send = (request, _) =>
		{
			Assert.Equal("Later", Assert.Single(request.RawRecords).Message);
			return Task.FromResult(Answer(request));
		};
		await fixture.Model.SendRequestAsync(RequestType.Reply);
	}

	[Fact]
	public async Task RecoveryReloadsDatabaseAndUnsavedFragmentsAndRejectsPreviousConnectionReply()
	{
		await using var fixture = new Fixture();
		await fixture.InitializeAsync();
		var timestamp = DateTime.UtcNow;
		await fixture.Processor.ProcessAsync(new DialogMessage(timestamp, AudioSourceType.Application, "Old"));
		var entered = new TaskCompletionSource<DialogRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource<DialogResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
		fixture.Connector.Send = (request, _) => { entered.SetResult(request); return release.Task; };
		var oldRequest = fixture.Model.SendRequestAsync(RequestType.Reply);
		var sent = await entered.Task;
		fixture.Database.Session = fixture.Database.Session with { Dialog = [new DialogRecord(timestamp, "interviewer", "Database replacement")] };
		fixture.Database.BeforeAppend = (_, _) => Task.FromException(new IOException("Offline"));
		await fixture.Processor.ProcessAsync(new DialogMessage(timestamp.AddSeconds(1), AudioSourceType.Microphone, "Unsaved"));
		var redrawn = 0;
		fixture.Dialog.HistoryReplaced += messages => { Assert.Equal(2, messages.Count); redrawn++; };
		fixture.Connector.Disconnect();
		await WaitUntilAsync(() => fixture.Model.IsReady && fixture.Connector.Reconnects == 1);
		Assert.Equal(1, redrawn);
		release.SetResult(Answer(sent));
		Assert.Null(await oldRequest);
		fixture.Connector.Send = (request, _) =>
		{
			Assert.Empty(request.RawRecords);
			Assert.Equal("Restored", request.Summary);
			return Task.FromResult(Answer(request));
		};
		await fixture.Model.SendRequestAsync(RequestType.Reply);
	}

	[Fact]
	public async Task ConcurrentRequestsHaveUniqueIncreasingIdentifiers()
	{
		await using var fixture = new Fixture();
		await fixture.InitializeAsync();
		var ids = new ConcurrentQueue<int>();
		fixture.Connector.Send = (request, _) => { ids.Enqueue(request.RequestId); return Task.FromResult(Answer(request)); };
		await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(() => fixture.Model.SendRequestAsync(RequestType.Reply))));
		Assert.Equal(100, ids.Distinct().Count());
	}

	[Fact]
	public async Task DisposalCancelsRequestAndIsIdempotent()
	{
		await using var fixture = new Fixture();
		await fixture.InitializeAsync();
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		fixture.Connector.Send = async (_, token) => { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return new DialogResponse(); };
		var request = fixture.Model.SendRequestAsync(RequestType.Reply);
		await entered.Task;
		await fixture.Model.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
		await fixture.Model.DisposeAsync();
		await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.Model.SendRequestAsync(RequestType.Reply));
	}

	[Fact]
	public async Task ConcurrentInitializationAndDisposalCancelTheSingleRemoteInitialization()
	{
		await using var fixture = new Fixture();
		await fixture.Selector.InitializeAsync();
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		fixture.Connector.Initialize = async (_, token) =>
		{
			entered.SetResult();
			await Task.Delay(Timeout.InfiniteTimeSpan, token);
			return new DialogResponse();
		};
		var first = fixture.Model.InitializeAsync();
		await entered.Task;
		var second = fixture.Model.InitializeAsync();
		await fixture.Model.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
		Assert.Equal(1, fixture.Connector.SessionInitializations);
	}

	[Fact]
	public async Task FailedInitializationCanBeRetriedWithoutPublishingReadyState()
	{
		await using var fixture = new Fixture();
		await fixture.Selector.InitializeAsync();
		fixture.Connector.Initialize = (_, _) => Task.FromResult(new DialogResponse { Error = "Failure" });
		await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Model.InitializeAsync());
		Assert.False(fixture.Model.IsReady);
		fixture.Connector.Initialize = (_, _) => Task.FromResult(new DialogResponse());
		await fixture.Model.InitializeAsync();
		Assert.True(fixture.Model.IsReady);
	}

	[Fact]
	public async Task RecoveryFailuresAreBoundedEvenIfEveryAttemptLosesConnection()
	{
		await using var fixture = new Fixture();
		await fixture.InitializeAsync();
		var attempts = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		fixture.Connector.Initialize = (_, _) =>
		{
			fixture.Connector.Disconnect();
			if (fixture.Connector.Reconnects == 3)
			{
				attempts.TrySetResult();
			}
			return Task.FromException<DialogResponse>(new IOException("Repeated disconnect"));
		};
		fixture.Connector.Disconnect();
		await attempts.Task.WaitAsync(TimeSpan.FromSeconds(5));
		await fixture.Model.DisposeAsync();
		Assert.Equal(3, fixture.Connector.Reconnects);
		Assert.False(fixture.Model.IsReady);
	}

	internal static async Task WaitUntilAsync(Func<bool> condition)
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		while (!condition())
		{
			await Task.Delay(10, timeout.Token);
		}
	}

	private static DialogResponse Answer(DialogRequest request, string message = "Answer") => new()
	{
		RequestId = request.RequestId, Summary = request.RawRecords.Count == 0 ? request.Summary : "Updated",
		SummaryTimestamp = request.RawRecords.LastOrDefault()?.Timestamp, Message = message
	};

	private sealed class Fixture : IAsyncDisposable
	{
		internal readonly TestSessionSelector Selector = new();
		internal readonly TestSessionDatabase Database = new();
		internal readonly TextProcessor Processor;
		internal readonly DialogCoordinator Dialog;
		internal readonly TestLLMConnector Connector = new();
		internal readonly SessionModel Model;
		internal Fixture()
		{
			Processor = new TextProcessor(Selector, Database, NullLogger<TextProcessor>.Instance);
			Dialog = new DialogCoordinator(Processor);
			Connector.Initialize = (session, _) => Task.FromResult(new DialogResponse
			{
				Summary = session.Dialog.Count == 0 ? "" : "Restored", SummaryTimestamp = session.Dialog.LastOrDefault()?.Timestamp
			});
			Model = new SessionModel(Selector, Database, Dialog, Connector, new ApplicationSettings(), NullLogger<SessionModel>.Instance,
				new SessionRecoveryOptions { Delay = TimeSpan.Zero });
		}
		internal async Task InitializeAsync() { await Selector.InitializeAsync(); await Model.InitializeAsync(); }
		public async ValueTask DisposeAsync() { await Model.DisposeAsync(); await Processor.DisposeAsync(); }
	}
}
