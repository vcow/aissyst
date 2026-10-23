using R3;

namespace aissyst.core.Audio;

public interface ICaptureSession
{
	Task InitializeAsync(CancellationToken cancellationToken = default);
	AudioInputDevice? SelectedMicrophone { get; }
	AudioApplication? SelectedApplication { get; }
	Observable<IReadOnlyList<AudioFrame>> MicrophoneRecordObservable { get; }
	Observable<IReadOnlyList<AudioFrame>> ApplicationRecordingObservable { get; }
}
