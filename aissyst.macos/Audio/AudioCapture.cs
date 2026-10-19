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

	public IAsyncEnumerable<AudioFrame> CaptureMicrophoneAsync(string deviceId, CancellationToken cancellationToken = default)
	{
		EnsureCaptureSupported();
		ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
		return NativeAudioCapture.ReadAsync("microphone", deviceId, cancellationToken);
	}

	public IAsyncEnumerable<AudioFrame> CaptureSystemAudioAsync(CancellationToken cancellationToken = default)
	{
		EnsureCaptureSupported();
		return NativeAudioCapture.ReadAsync("system", null, cancellationToken);
	}

	public IAsyncEnumerable<AudioFrame> CaptureApplicationAudioAsync(AudioApplication application,
		CancellationToken cancellationToken = default)
	{
		EnsureCaptureSupported();
		ArgumentNullException.ThrowIfNull(application);
		if (application.ProcessIds.Count == 0 || application.ProcessIds.Any(pid => pid <= 0))
		{
			throw new ArgumentException("The selected application has no valid audio processes.", nameof(application));
		}
		return NativeAudioCapture.ReadAsync("application", string.Join(",", application.ProcessIds.Distinct()), cancellationToken);
	}

	private static void EnsureCaptureSupported()
	{
		if (!OperatingSystem.IsMacOSVersionAtLeast(14, 2))
		{
			throw new PlatformNotSupportedException("Audio capture requires macOS 14.2 or later.");
		}
	}
}
