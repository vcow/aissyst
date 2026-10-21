using aissyst.console;
using aissyst.core;
using aissyst.core.Audio;
using aissyst.core.Transcription;
using Microsoft.Extensions.Logging;

namespace aissyst.tests;

public class TranscriptionQueueTests
{
	[Fact]
	public async Task OverflowDropsOldestRecordsAndReportsEachRemoval()
	{
		var logger = new TestLogger();
		var queue = new TranscriptionQueue(new ApplicationSettings(transcriptionQueueCapacity: 2), logger);
		var first = Request();
		var second = Request();
		var third = Request();
		var fourth = Request();
		queue.Enqueue(first);
		queue.Enqueue(second);
		queue.Enqueue(third);
		queue.Enqueue(fourth);

		Assert.Equal(2, queue.Count);
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		Assert.Same(third, await queue.DequeueAsync(timeout.Token));
		Assert.Same(fourth, await queue.DequeueAsync(timeout.Token));
		Assert.Equal(2, logger.Warnings.Count);
		Assert.Contains(first.Id.ToString(), logger.Warnings[0]);
		Assert.Contains(second.Id.ToString(), logger.Warnings[1]);
	}

	[Fact]
	public async Task ClearCountsOnlyPendingRecordsAndAcceptsNewRecords()
	{
		var queue = new TranscriptionQueue(new ApplicationSettings(), new TestLogger());
		var active = Request();
		queue.Enqueue(active);
		queue.Enqueue(Request());
		queue.Enqueue(Request());
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		Assert.Same(active, await queue.DequeueAsync(timeout.Token));
		Assert.Equal(2, queue.Clear());
		Assert.Equal(0, queue.Clear());
		Assert.Equal(0, queue.Count);

		var next = Request();
		var pendingRead = queue.DequeueAsync(timeout.Token).AsTask();
		Assert.False(pendingRead.IsCompleted);
		queue.Enqueue(next);
		Assert.Same(next, await pendingRead);
	}

	[Fact]
	public async Task EmptyQueueWaitCanBeCancelledWithoutLosingSubsequentRecords()
	{
		var queue = new TranscriptionQueue(new ApplicationSettings(), new TestLogger());
		using var cancellation = new CancellationTokenSource();
		var read = queue.DequeueAsync(cancellation.Token).AsTask();
		cancellation.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
		var next = Request();
		queue.Enqueue(next);
		Assert.Same(next, await queue.DequeueAsync());
	}

	[Fact]
	public async Task ConcurrentSourcesShareOneQueueWithoutLosingRecordsBelowCapacity()
	{
		var queue = new TranscriptionQueue(new ApplicationSettings(transcriptionQueueCapacity: 100), new TestLogger());
		var requests = Enumerable.Range(0, 100).Select(_ => Request()).ToArray();
		await Task.WhenAll(requests.Chunk(25).Select(batch => Task.Run(() =>
		{
			foreach (var request in batch)
			{
				queue.Enqueue(request);
			}
		})));
		Assert.Equal(100, queue.Count);
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		var received = new HashSet<Guid>();
		for (var index = 0; index < requests.Length; index++)
		{
			received.Add((await queue.DequeueAsync(timeout.Token)).Id);
		}
		Assert.True(received.SetEquals(requests.Select(request => request.Id)));
	}

	private static TranscriptionRequest Request()
	{
		return new TranscriptionRequest(Guid.NewGuid(), "test-session", AudioSourceType.Application,
			DateTimeOffset.UtcNow, new AudioRecording(new float[] { 0.5f }, new AudioFormat(16000, 1)));
	}

	private sealed class TestLogger : ILogger<TranscriptionQueue>
	{
		internal List<string> Warnings { get; } = [];
		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
		public bool IsEnabled(LogLevel logLevel) => true;
		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
			Func<TState, Exception?, string> formatter)
		{
			if (logLevel == LogLevel.Warning)
			{
				Warnings.Add(formatter(state, exception));
			}
		}
	}
}
