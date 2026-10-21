using aissyst.core.Audio;

namespace aissyst.core.Transcription;

public sealed record TranscriptionRequest(
	Guid Id,
	string SessionId,
	AudioSourceType Source,
	DateTimeOffset CompletedAt,
	AudioRecording Recording);
