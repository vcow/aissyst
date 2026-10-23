using aissyst.core.Controller;
using Microsoft.Extensions.Logging;

namespace aissyst.tests;

public class BackgroundOperationTests
{
	[Fact]
	public async Task SynchronousFailureIsLoggedWithOperationName()
	{
		var logger = new TestLogger();
		var error = new InvalidOperationException("Synchronous failure.");
		await BackgroundOperation.RunAsync(() => throw error, logger, "EnqueueAudio", CancellationToken.None);

		var entry = Assert.Single(logger.Errors);
		Assert.Same(error, entry.Exception);
		Assert.Contains("EnqueueAudio", entry.Message);
	}

	[Fact]
	public async Task FailureAfterAwaitIsLogged()
	{
		var logger = new TestLogger();
		var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var observation = BackgroundOperation.RunAsync(() => pending.Task, logger, "Transcribe", CancellationToken.None);
		Assert.False(observation.IsCompleted);
		var error = new IOException("Asynchronous failure.");
		pending.SetException(error);
		await observation;

		Assert.Same(error, Assert.Single(logger.Errors).Exception);
	}

	[Fact]
	public async Task FailureInFinallyIsLogged()
	{
		var logger = new TestLogger();
		var error = new SemaphoreFullException();
		async Task Operation()
		{
			try
			{
				await Task.Yield();
			}
			finally
			{
				throw error;
			}
		}

		await BackgroundOperation.RunAsync(Operation, logger, "Release", CancellationToken.None);
		Assert.Same(error, Assert.Single(logger.Errors).Exception);
	}

	[Fact]
	public async Task ShutdownCancellationIsNotLoggedAsAnError()
	{
		var logger = new TestLogger();
		using var cancellation = new CancellationTokenSource();
		var observation = BackgroundOperation.RunAsync(
			() => Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token), logger, "WaitAsync", cancellation.Token);
		cancellation.Cancel();
		await observation;
		Assert.Empty(logger.Errors);
	}

	[Fact]
	public async Task UnexpectedCancellationIsLogged()
	{
		var logger = new TestLogger();
		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();
		await BackgroundOperation.RunAsync(() => Task.FromCanceled(cancellation.Token),
			logger, "Transcribe", CancellationToken.None);
		Assert.IsAssignableFrom<OperationCanceledException>(Assert.Single(logger.Errors).Exception);
	}

	[Fact]
	public async Task AlreadyHandledFailureIsNotLoggedTwice()
	{
		var logger = new TestLogger();
		Task Operation()
		{
			try
			{
				throw new IOException("Handled failure.");
			}
			catch (IOException exception)
			{
				logger.LogError(exception, "Operation failed.");
			}
			return Task.CompletedTask;
		}

		await BackgroundOperation.RunAsync(Operation, logger, "Transcribe", CancellationToken.None);
		Assert.Single(logger.Errors);
	}

	private sealed class TestLogger : ILogger
	{
		internal List<(Exception? Exception, string Message)> Errors { get; } = [];
		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
		public bool IsEnabled(LogLevel logLevel) => true;
		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
			Func<TState, Exception?, string> formatter)
		{
			if (logLevel == LogLevel.Error)
			{
				Errors.Add((exception, formatter(state, exception)));
			}
		}
	}
}
