using aissyst.core;
using aissyst.core.Database;
using aissyst.core.Processor;
using Microsoft.Extensions.Logging.Abstractions;

namespace aissyst.tests;

public sealed class TextProcessorInitializationTests
{
	[Fact]
	public async Task SelectorMustBeInitializedBeforeLoadingHistory()
	{
		var selector = new TestSessionSelector();
		var database = new TestSessionDatabase();
		await using var processor = new TextProcessor(selector, database, NullLogger<TextProcessor>.Instance);
		await Assert.ThrowsAsync<InvalidOperationException>(() => processor.InitializeAsync());
		Assert.Empty(database.RequestedSessions);
		Assert.False(processor.IsReady);
		await selector.InitializeAsync();
		await processor.InitializeAsync();
		Assert.Equal(1, selector.Calls);
		Assert.Equal(7, Assert.Single(database.RequestedSessions));
	}

	[Fact]
	public async Task InitializationRestoresAllRecordsIncludingEqualTimestampsAndMapsRoles()
	{
		var start = DateTime.UtcNow;
		var database = new TestSessionDatabase();
		database.Session = database.Session with { Dialog = [
			new DialogRecord(start.AddSeconds(2), "interviewee", "Answer."),
			new DialogRecord(start, "interviewer", "First."),
			new DialogRecord(start, "interviewer", "Second."),
			new DialogRecord(start.AddSeconds(3), "unknown", "Unknown speaker.")
		] };
		var selector = new TestSessionSelector();
		await using var processor = await CreateAsync(selector, database);
		Assert.False(processor.IsReady);
		await processor.InitializeAsync();
		Assert.True(processor.IsReady);
		Assert.Equal(1, selector.Calls);
		Assert.Equal(new[]
		{
			new DialogMessage(start, AudioSourceType.Application, "First. Second."),
			new DialogMessage(start.AddSeconds(2), AudioSourceType.Microphone, "Answer."),
			new DialogMessage(start.AddSeconds(3), AudioSourceType.None, "Unknown speaker.")
		}, processor.GetDialog(DateTime.MinValue));
		// History has already been displayed. New input must not emit it again.
		Assert.Null(processor.Process(new DialogMessage(start.AddSeconds(4), AudioSourceType.Application, "New turn.")));
		await processor.InitializeAsync();
		Assert.Single(database.RequestedSessions);
	}

	[Fact]
	public async Task ConcurrentInitializationLoadsOnceAndDoesNotExposePartialHistory()
	{
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var database = new TestSessionDatabase();
		database.Load = async (_, cancellationToken) =>
		{
			entered.TrySetResult();
			await release.Task.WaitAsync(cancellationToken);
			return database.Session;
		};
		var selector = new TestSessionSelector();
		await using var processor = await CreateAsync(selector, database);
		var first = processor.InitializeAsync();
		await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
		var second = processor.InitializeAsync();
		Assert.False(processor.IsReady);
		Assert.Throws<InvalidOperationException>(() => processor.Process(new DialogMessage(DateTime.UtcNow, AudioSourceType.Microphone, "Too early.")));
		release.TrySetResult();
		await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Single(database.RequestedSessions);
		Assert.Equal(1, selector.Calls);
	}

	[Fact]
	public async Task FailedLoadCanBeRetriedWithoutPublishingPartialRecords()
	{
		var database = new TestSessionDatabase();
		database.Session = database.Session with { Dialog = [new DialogRecord(DateTime.UtcNow, "invalid-role", "Text")] };
		await using var processor = await CreateAsync(new TestSessionSelector(), database);
		await Assert.ThrowsAsync<InvalidDataException>(() => processor.InitializeAsync());
		Assert.False(processor.IsReady);
		Assert.Empty(processor.GetDialog(DateTime.MinValue));
		database.Session = database.Session with { Dialog = [] };
		await processor.InitializeAsync();
		Assert.True(processor.IsReady);
	}

	[Fact]
	public async Task DisposalCancelsPendingLoadAndWaitsForItBeforeReleasingResources()
	{
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var database = new TestSessionDatabase();
		database.Load = async (_, cancellationToken) =>
		{
			entered.TrySetResult();
			await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
			return database.Session;
		};
		var processor = await CreateAsync(new TestSessionSelector(), database);
		var initialization = processor.InitializeAsync();
		await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
		await processor.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialization);
		Assert.False(processor.IsReady);
		await processor.DisposeAsync();
		await Assert.ThrowsAsync<ObjectDisposedException>(() => processor.InitializeAsync());
		Assert.Throws<ObjectDisposedException>(() => processor.Process(new DialogMessage(DateTime.UtcNow, AudioSourceType.Microphone, "After disposal.")));
	}

	internal static async Task<TextProcessor> CreateAsync(TestSessionSelector selector, TestSessionDatabase database)
	{
		await selector.InitializeAsync();
		return new TextProcessor(selector, database, NullLogger<TextProcessor>.Instance);
	}
}
