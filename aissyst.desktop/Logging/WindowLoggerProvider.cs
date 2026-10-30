using Microsoft.Extensions.Logging;

namespace aissyst.desktop.Logging;

internal sealed class WindowLoggerProvider(Action<LogEntry> append) : ILoggerProvider
{
	private readonly Action<LogEntry> _append = append;
	private int _disposed;

	public ILogger CreateLogger(string categoryName) => new WindowLogger(this, categoryName);

	public void Dispose() => Interlocked.Exchange(ref _disposed, 1);

	private sealed class WindowLogger(WindowLoggerProvider provider, string categoryName) : ILogger
	{
		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

		public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && Volatile.Read(ref provider._disposed) == 0;

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
			Func<TState, Exception?, string> formatter)
		{
			if (!IsEnabled(logLevel))
			{
				return;
			}

			var message = formatter(state, exception);
			if (string.IsNullOrEmpty(message) && exception == null)
			{
				return;
			}

			provider._append(new LogEntry(DateTimeOffset.Now, logLevel, categoryName, message, exception?.ToString()));
		}
	}
}
