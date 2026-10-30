using Avalonia.Media;
using Microsoft.Extensions.Logging;

namespace aissyst.desktop.Logging;

internal sealed record LogEntry(DateTimeOffset Timestamp, LogLevel Level, string Category, string Message, string? ExceptionText)
{
	public string Text { get; } = $"{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level}] {Category}: {Message}"
		+ (ExceptionText == null ? string.Empty : Environment.NewLine + ExceptionText);

	public IBrush Foreground => Level switch
	{
		LogLevel.Warning => Brushes.Yellow,
		LogLevel.Error or LogLevel.Critical => Brushes.Red,
		_ => Brushes.White
	};
}
