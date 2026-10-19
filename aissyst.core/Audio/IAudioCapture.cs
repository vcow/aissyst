namespace aissyst.core.Audio;

public enum AudioSourceType
{
	Microphone,
	SystemOut,
	Application
}

public readonly record struct AudioFormat(
	int SampleRate,
	int Channels);

public sealed record AudioFrame(
	ReadOnlyMemory<float> Samples,
	AudioFormat Format,
	TimeSpan Timestamp);

public sealed record AudioApplication(
	int Id,
	string Name,
	bool IsOutputActive,
	IReadOnlyList<int> ProcessIds);

public sealed record AudioInputDevice(
	string Id,
	string Name,
	int Channels,
	bool isDefault);

public interface IAudioCapture
{
	Task<IReadOnlyList<AudioInputDevice>> GetInputDevicesAsync(CancellationToken cancellationToken = default);
	Task<IReadOnlyList<AudioApplication>> GetApplicationsAsync(CancellationToken cancellationToken = default);
	IAsyncEnumerable<AudioFrame> CaptureMicrophoneAsync(CancellationToken cancellationToken = default);
	IAsyncEnumerable<AudioFrame> CaptureSystemAudioAsync(CancellationToken cancellationToken = default);
	IAsyncEnumerable<AudioFrame> CaptureApplicationAudioAsync(string applicationId,
		CancellationToken cancellationToken = default);
}
