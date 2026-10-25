using aissyst.console;
using aissyst.core;
using aissyst.core.Database;
using aissyst.core.LLMConnector.DTO;
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
		Assert.True(newer.Id > older.Id);
		model.Reset(new DialogResponse { RequestId = older.Id, Summary = "Stale.", SummaryTimestamp = timestamp });
		model.Reset(new DialogResponse { RequestId = newer.Id, Summary = "Accepted.", SummaryTimestamp = timestamp });
		model.Reset(new DialogResponse { RequestId = newer.Id, Summary = "Duplicate.", SummaryTimestamp = timestamp });
		var emptyRequest = model.GetRequest(RequestType.Advice);
		Assert.Equal("Accepted.", emptyRequest.Summary);
		Assert.Empty(emptyRequest.RawRecords);
		model.Reset(new DialogResponse { RequestId = emptyRequest.Id, Summary = "Updated without new dialog." });
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
			RequestId = request.Id, Summary = "Invalid future boundary.", SummaryTimestamp = timestamp.AddSeconds(1)
		}));
		model.Reset(new DialogResponse { RequestId = request.Id, Summary = "First only.", SummaryTimestamp = timestamp });
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
		Assert.Equal(100, requests.Select(request => request.Id).Distinct().Count());
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
		model.Reset(new DialogResponse { RequestId = request.Id, Error = "Temporary failure." });
		Assert.Throws<InvalidDataException>(() => model.Reset(new DialogResponse { RequestId = request.Id }));
		Assert.Equal("Initial summary.", model.GetRequest(RequestType.Reply).Summary);
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
