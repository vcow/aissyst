namespace aissyst.core.Audio;

public readonly record struct AudioFormat(
	int SampleRate,
	int Channels);

public sealed record AudioFrame(
	ReadOnlyMemory<float> Samples,
	AudioFormat Format,
	TimeSpan Timestamp);

public sealed record AudioRecording(ReadOnlyMemory<float> Samples, AudioFormat Format);

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
	IAsyncEnumerable<AudioFrame> CaptureMicrophoneAsync(string deviceId, CancellationToken cancellationToken = default);
	IAsyncEnumerable<AudioFrame> CaptureApplicationAudioAsync(AudioApplication application,
		CancellationToken cancellationToken = default);
}
