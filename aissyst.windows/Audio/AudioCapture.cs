using aissyst.core.Audio;

namespace aissyst.windows.Audio;

public class AudioCapture : IAudioCapture
{
	public Task<IReadOnlyList<AudioInputDevice>> GetInputDevicesAsync(CancellationToken cancellationToken = default)
	{
		throw new NotImplementedException();
	}

	public Task<IReadOnlyList<AudioApplication>> GetApplicationsAsync(CancellationToken cancellationToken = default)
	{
		throw new NotImplementedException();
	}

	public IAsyncEnumerable<AudioFrame> CaptureMicrophoneAsync(string deviceId, CancellationToken cancellationToken = default)
	{
		throw new NotImplementedException();
	}

	public IAsyncEnumerable<AudioFrame> CaptureApplicationAudioAsync(AudioApplication application,
		CancellationToken cancellationToken = default)
	{
		throw new NotImplementedException();
	}
}
