using aissyst.core.Audio;
using aissyst.macos.Interop;

namespace aissyst.macos.Audio;

public class AudioCapture : IAudioCapture
{
	public Task<IReadOnlyList<AudioInputDevice>> GetInputDevicesAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (!OperatingSystem.IsMacOS())
		{
			throw new PlatformNotSupportedException("Core Audio device enumeration is available only on macOS.");
		}

		return Task.FromResult(CoreAudioDevices.GetInputDevices(cancellationToken));
	}

	public Task<IReadOnlyList<AudioApplication>> GetApplicationsAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (!OperatingSystem.IsMacOSVersionAtLeast(14, 2))
		{
			throw new PlatformNotSupportedException("Audio application enumeration requires macOS 14.2 or later.");
		}

		return Task.FromResult(MacApplications.GetApplications(cancellationToken));
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
