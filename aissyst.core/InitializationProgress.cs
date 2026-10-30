namespace aissyst.core;

public sealed record InitializationProgress(string Message, long? CompletedBytes = null, long? TotalBytes = null);
