using aissyst.console;
using aissyst.core;
using aissyst.core.Database;
using aissyst.core.LLMConnector.DTO;
using aissyst.core.LLMConnector;
using aissyst.core.LLMConnector.Model;
using aissyst.core.Processor;
using Microsoft.Extensions.Logging.Abstractions;

namespace aissyst.tests;

public sealed class SessionModelTests
{
	[Fact]
	public async Task InitializationSelectsOnceAndSendsRestoredDialogAndContextToLlm()
	{
		var timestamp = DateTime.UtcNow;
		var database = new TestSessionDatabase();
		database.Session = database.Session with { Dialog = [new DialogRecord(timestamp, "interviewer", "Stored question.")] };
		var selector = new TestSessionSelector();
		await using var processor = await TextProcessorInitializationTests.CreateAsync(selector, database);
		var connector = new TestLLMConnector
		{
			Initialize = (session, _) =>
			{
				Assert.True(processor.IsReady);
				Assert.Equal("Test preview", session.Preview);
				Assert.Equal("Background", Assert.Single(session.Context).Topic);
				Assert.Equal(SenderRole.Interviewer, Assert.Single(session.Dialog).Role);
				Assert.Equal("Stored question.", session.Dialog[0].Message);
				return Task.FromResult(new DialogResponse { Summary = "Initial summary.", SummaryTimestamp = timestamp });
			}
		};
		await using var model = Create(selector, database, processor, connector);
		await model.InitializeAsync();
		Assert.Equal(1, selector.Calls);
		Assert.All(database.RequestedSessions, id => Assert.Equal(7, id));
		Assert.Equal(1, connector.SessionInitializations);
		var request = model.GetRequest(RequestType.Advice);
		Assert.Equal("Initial summary.", request.Summary);
		Assert.Empty(request.RawRecords);
		Assert.Single(model.GetSession().Dialog);
		await Assert.ThrowsAsync<InvalidOperationException>(() => model.InitializeAsync());
	}

	[Fact]
	public async Task EmptyDialogAcceptsMissingSummaryTimestampAndOnlyLatestRequestCanUpdateSummary()
	{
		var database = new TestSessionDatabase();
		var selector = new TestSessionSelector();
		await using var processor = await TextProcessorInitializationTests.CreateAsync(selector, database);
		await using var model = Create(selector, database, processor, SuccessfulConnector());
		await model.InitializeAsync();
		Assert.Empty(model.GetSession().Dialog);
		var timestamp = DateTime.UtcNow;
		processor.Process(new DialogMessage(timestamp, AudioSourceType.Application, "Question."));
		var older = model.GetRequest(RequestType.Translate);
		var newer = model.GetRequest(RequestType.Reply);
		Assert.True(newer.RequestId > older.RequestId);
		model.Reset(new DialogResponse { RequestId = older.RequestId, Summary = "Stale.", SummaryTimestamp = timestamp });
		model.Reset(new DialogResponse { RequestId = newer.RequestId, Summary = "Accepted.", SummaryTimestamp = timestamp });
		model.Reset(new DialogResponse { RequestId = newer.RequestId, Summary = "Duplicate.", SummaryTimestamp = timestamp });
		var emptyRequest = model.GetRequest(RequestType.Advice);
		Assert.Equal("Accepted.", emptyRequest.Summary);
		Assert.Empty(emptyRequest.RawRecords);
		model.Reset(new DialogResponse { RequestId = emptyRequest.RequestId, Summary = "Updated without new dialog." });
		Assert.Empty(model.GetRequest(RequestType.Summary).RawRecords);
	}

	[Fact]
	public async Task ResponseBoundaryIsValidatedAgainstSentSnapshotAndNewerMessagesRemainRaw()
	{
		var database = new TestSessionDatabase();
		var selector = new TestSessionSelector();
		await using var processor = await TextProcessorInitializationTests.CreateAsync(selector, database);
		await using var model = Create(selector, database, processor, SuccessfulConnector());
		await model.InitializeAsync();
		var timestamp = DateTime.UtcNow;
		processor.Process(new DialogMessage(timestamp, AudioSourceType.Application, "First."));
		var request = model.GetRequest(RequestType.Advice);
		processor.Process(new DialogMessage(timestamp.AddSeconds(1), AudioSourceType.Application, "Continuation."));
		Assert.Throws<InvalidDataException>(() => model.Reset(new DialogResponse
		{
			RequestId = request.RequestId, Summary = "Invalid future boundary.", SummaryTimestamp = timestamp.AddSeconds(1)
		}));
		model.Reset(new DialogResponse { RequestId = request.RequestId, Summary = "First only.", SummaryTimestamp = timestamp });
		var next = model.GetRequest(RequestType.Reply);
		Assert.Equal("First only.", next.Summary);
		Assert.Equal("Continuation.", Assert.Single(next.RawRecords).Message);
	}

	[Fact]
	public async Task LlmFailureDoesNotMarkModelReadyAndInitializationCanBeRetried()
	{
		var database = new TestSessionDatabase();
		var selector = new TestSessionSelector();
		await using var processor = await TextProcessorInitializationTests.CreateAsync(selector, database);
		var connector = new TestLLMConnector { Initialize = (_, _) => Task.FromResult(new DialogResponse { Error = "Provider failure." }) };
		await using var model = Create(selector, database, processor, connector);
		await Assert.ThrowsAsync<InvalidOperationException>(() => model.InitializeAsync());
		Assert.Throws<InvalidOperationException>(() => model.GetSession());
		connector.Initialize = (_, _) => Task.FromResult(new DialogResponse());
		await model.InitializeAsync();
		Assert.Empty(model.GetSession().Dialog);
	}

	[Fact]
	public async Task ConcurrentInitializationAndDisposalShareOneOperationAndPropagateCancellationToLlm()
	{
		var database = new TestSessionDatabase();
		var selector = new TestSessionSelector();
		await using var processor = await TextProcessorInitializationTests.CreateAsync(selector, database);
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var connector = new TestLLMConnector
		{
			Initialize = async (_, cancellationToken) =>
			{
				entered.TrySetResult();
				await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
				return new DialogResponse();
			}
		};
		var model = Create(selector, database, processor, connector);
		var first = model.InitializeAsync();
		await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
		var second = model.InitializeAsync();
		await model.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
		await model.DisposeAsync();
		Assert.Equal(1, selector.Calls);
		Assert.Equal(1, connector.SessionInitializations);
		Assert.Throws<ObjectDisposedException>(() => model.GetSession());
	}

	[Fact]
	public async Task ConcurrentRequestsHaveUniqueIncreasingIdentifiers()
	{
		var database = new TestSessionDatabase();
		var selector = new TestSessionSelector();
		await using var processor = await TextProcessorInitializationTests.CreateAsync(selector, database);
		await using var model = Create(selector, database, processor, SuccessfulConnector());
		await model.InitializeAsync();
		var requests = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(() => model.GetRequest(RequestType.Reply))));
		Assert.Equal(100, requests.Select(request => request.RequestId).Distinct().Count());
	}

	[Fact]
	public async Task EmptyOrErrorResponseCannotEraseExistingSummary()
	{
		var database = new TestSessionDatabase();
		database.Session = database.Session with { Dialog = [new DialogRecord(DateTime.UtcNow, "interviewer", "Stored question.")] };
		var selector = new TestSessionSelector();
		await using var processor = await TextProcessorInitializationTests.CreateAsync(selector, database);
		await using var model = Create(selector, database, processor, SuccessfulConnector());
		await model.InitializeAsync();
		var request = model.GetRequest(RequestType.Advice);
		model.Reset(new DialogResponse { RequestId = request.RequestId, Error = "Temporary failure." });
		Assert.Throws<InvalidDataException>(() => model.Reset(new DialogResponse { RequestId = request.RequestId }));
		Assert.Equal("Initial summary.", model.GetRequest(RequestType.Reply).Summary);
	}

	[Fact]
	public async Task RecoveryUsesFreshDialogAndReplacesSummaryWithoutLosingLaterFragments()
	{
		var database = new TestSessionDatabase();
		var selector = new TestSessionSelector();
		await using var processor = await TextProcessorInitializationTests.CreateAsync(selector, database);
		var connector = new LifecycleConnector();
		await using var model = new SessionModel(selector, database, processor, connector,
			new ApplicationSettings(), NullLogger<SessionModel>.Instance);
		await model.InitializeAsync();
		Assert.Equal("Test initial instructions", connector.Instructions);
		var first = DateTime.UtcNow;
		processor.Process(new DialogMessage(first, AudioSourceType.Application, "First"));
		var obsolete = model.GetRequest(RequestType.Reply);
		database.Session = database.Session with { Dialog = [new DialogRecord(first, "interviewer", "Database replacement")] };
		var snapshot = await connector.GetSession!(default);
		Assert.Equal("Database replacement", Assert.Single(snapshot.Dialog).Message);
		processor.Process(new DialogMessage(first.AddSeconds(1), AudioSourceType.Application, "Later"));
		connector.AcceptRecovery!(snapshot, new DialogResponse { Summary = "Rebuilt summary", SummaryTimestamp = first });
		Assert.False(connector.AcceptResponse!(new DialogResponse { RequestId = obsolete.RequestId, Summary = "Old", SummaryTimestamp = first }));
		var next = model.GetRequest(RequestType.Reply);
		Assert.Equal("Rebuilt summary", next.Summary);
		Assert.Equal("Later", Assert.Single(next.RawRecords).Message);
		Assert.True(connector.AcceptResponse!(new DialogResponse { RequestId = next.RequestId, Summary = "Current", SummaryTimestamp = first.AddSeconds(1) }));
		Assert.Empty(model.GetRequest(RequestType.Summary).RawRecords);
	}

	private sealed class LifecycleConnector : ILLMConnector, ILLMSessionLifecycle
	{
		internal string? Instructions;
		internal Func<CancellationToken, Task<Session>>? GetSession;
		internal Action<Session, DialogResponse>? AcceptRecovery;
		internal Func<DialogResponse, bool>? AcceptResponse;
		public void ConfigureSession(string instructions, Func<CancellationToken, Task<Session>> getSession,
			Action<Session, DialogResponse> acceptRecovery, Func<DialogResponse, bool> acceptResponse)
		{
			Instructions = instructions;
			GetSession = getSession;
			AcceptRecovery = acceptRecovery;
			AcceptResponse = acceptResponse;
		}
		public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
		public Task<DialogResponse> InitializeSession(Session session, CancellationToken cancellationToken = default) =>
			Task.FromResult(new DialogResponse());
		public Task<DialogResponse> SendRequest(DialogRequest request, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();
	}

	[Fact]
	public async Task OlderValidResponseIsAcceptedWhileNewerRequestIsPendingOrFailed()
	{
		var database = new TestSessionDatabase();
		var selector = new TestSessionSelector();
		await using var processor = await TextProcessorInitializationTests.CreateAsync(selector, database);
		await using var model = Create(selector, database, processor, SuccessfulConnector());
		await model.InitializeAsync();
		var firstTime = DateTime.UtcNow;
		await processor.ProcessAsync(new DialogMessage(firstTime, AudioSourceType.Application, "First"));
		var first = model.GetRequest(RequestType.Advice);
		await processor.ProcessAsync(new DialogMessage(firstTime.AddSeconds(1), AudioSourceType.Microphone, "Second"));
		var second = model.GetRequest(RequestType.Reply);
		Assert.False(model.Reset(new DialogResponse { RequestId = second.RequestId, Error = "Failed" }));
		Assert.True(model.Reset(new DialogResponse { RequestId = first.RequestId, Summary = "First summary", SummaryTimestamp = firstTime }));
		var next = model.GetRequest(RequestType.Translate);
		Assert.Equal("First summary", next.Summary);
		Assert.Equal("Second", Assert.Single(next.RawRecords).Message);
		Assert.Throws<InvalidDataException>(() => model.Reset(new DialogResponse { RequestId = second.RequestId, Summary = "Bad", SummaryTimestamp = firstTime }));
		Assert.True(model.Reset(new DialogResponse { RequestId = second.RequestId, Summary = "Both", SummaryTimestamp = firstTime.AddSeconds(1) }));
		Assert.False(model.Reset(new DialogResponse { RequestId = first.RequestId, Summary = "Old", SummaryTimestamp = firstTime }));
	}

	private static TestLLMConnector SuccessfulConnector() => new()
	{
		Initialize = (session, _) => Task.FromResult(new DialogResponse
		{
			Summary = session.Dialog.Count == 0 ? string.Empty : "Initial summary.",
			SummaryTimestamp = session.Dialog.LastOrDefault()?.Timestamp
		})
	};

	private static SessionModel Create(TestSessionSelector selector, TestSessionDatabase database,
		TextProcessor processor, TestLLMConnector connector) =>
		new(selector, database, processor, connector, new ApplicationSettings(), NullLogger<SessionModel>.Instance);
}
