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

	public IAsyncEnumerable<AudioFrame> CaptureMicrophoneAsync(CancellationToken cancellationToken = default)
	{
		throw new NotImplementedException();
	}

	public IAsyncEnumerable<AudioFrame> CaptureSystemAudioAsync(CancellationToken cancellationToken = default)
	{
		throw new NotImplementedException();
	}

	public IAsyncEnumerable<AudioFrame> CaptureApplicationAudioAsync(string applicationId,
		CancellationToken cancellationToken = default)
	{
		throw new NotImplementedException();
	}
}