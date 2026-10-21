namespace aissyst.core;

[Flags]
public enum AudioSourceType
{
	None = 0x0,
	Microphone = 0x1,
	Application = 0x2,
	Both = Microphone | Application
}

public interface IApplicationSettings
{
	AudioSourceType AvailableSources { get; }
	TimeSpan PauseUntilRecordingStops { get; }
	float SignalThreshold { get; }
	string TranscriptionModelPath { get; }
	Uri TranscriptionModelUrl { get; }
	string TranscriptionModelSha256 { get; }
	string TranscriptionLanguage { get; }
}
