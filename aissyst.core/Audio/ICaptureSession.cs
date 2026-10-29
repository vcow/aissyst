namespace aissyst.core.Audio;

public interface ICaptureSession
{
	Task InitializeAsync(CancellationToken cancellationToken = default);
	AudioInputDevice? SelectedMicrophone { get; }
	AudioApplication? SelectedApplication { get; }
	event Action<IReadOnlyList<AudioFrame>>? MicrophoneRecordingCompleted;
	event Action<IReadOnlyList<AudioFrame>>? ApplicationRecordingCompleted;
}
