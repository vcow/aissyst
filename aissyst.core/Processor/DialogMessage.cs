namespace aissyst.core.Processor;

public sealed record DialogMessage(
	DateTime Timestamp,
	AudioSourceType Source,
	string Text);