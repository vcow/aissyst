using Microsoft.Extensions.Logging;

namespace aissyst.core.Processor;

public sealed class LoggingTextProcessor(ILogger<LoggingTextProcessor> logger) : ITextProcessor
{
	public bool IsReady { get; private set; }

	public Task InitializeAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		IsReady = true;
		return Task.CompletedTask;
	}

	public Task ProcessAsync(DateTime timestamp, AudioSourceType source, string text,
		CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (!IsReady)
		{
			throw new InvalidOperationException("The text processor has not been initialized.");
		}
		if (!string.IsNullOrWhiteSpace(text))
		{
			logger.LogInformation("{Source} [{Timestamp:HH:mm:ss}]: {Text}", source, timestamp, text);
		}
		return Task.CompletedTask;
	}
}
