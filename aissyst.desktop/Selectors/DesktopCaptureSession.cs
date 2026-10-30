using System.Globalization;
using aissyst.core;
using aissyst.core.Audio;
using aissyst.desktop.Wizard;
using Microsoft.Extensions.Logging;

namespace aissyst.desktop.Selectors;

internal sealed class DesktopCaptureSession(IAudioCapture capture, IApplicationSettings settings,
	IWizardService wizard, ILogger<DesktopCaptureSession> logger) : ICaptureSession, IDisposable
{
	private bool _initialized;
	private bool _disposed;
	public AudioInputDevice? SelectedMicrophone { get; private set; }
	public AudioApplication? SelectedApplication { get; private set; }
	public event Action<IReadOnlyList<AudioFrame>>? MicrophoneRecordingCompleted;
	public event Action<IReadOnlyList<AudioFrame>>? ApplicationRecordingCompleted;

	public async Task InitializeAsync(CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		cancellationToken.ThrowIfCancellationRequested();
		if (_initialized)
		{
			return;
		}
		if (settings.AvailableSources == AudioSourceType.None || (settings.AvailableSources & ~AudioSourceType.Both) != 0)
		{
			throw new InvalidOperationException("No supported audio sources are enabled.");
		}
		var error = string.Empty;
		while (true)
		{
			AudioInputDevice? microphone = null;
			AudioApplication? application = null;
			if (settings.AvailableSources.HasFlag(AudioSourceType.Microphone))
			{
				var devices = await capture.GetInputDevicesAsync(cancellationToken);
				var options = new[] { new WizardOption("none", "None") }.Concat(devices.Select(device =>
					new WizardOption(device.Id, device.Name, device.isDefault ? "Default microphone" : ""))).ToArray();
				var selected = await wizard.ShowAsync(new WizardPage("Select a microphone",
					"Your microphone identifies your side of the dialog. Choose None to capture only the other speaker.", options)
				{
					SelectedKey = devices.FirstOrDefault(device => device.isDefault)?.Id ?? "none", Error = error
				}, cancellationToken);
				microphone = selected == "none" ? null : devices.First(device => device.Id == selected);
			}
			if (settings.AvailableSources.HasFlag(AudioSourceType.Application))
			{
				var applications = await capture.GetApplicationsAsync(cancellationToken);
				var options = new[] { new WizardOption("none", "None") }.Concat(applications.Select(app =>
					new WizardOption(app.Id.ToString(CultureInfo.InvariantCulture), app.Name,
						app.IsOutputActive ? "Currently playing audio" : ""))).ToArray();
				var selected = await wizard.ShowAsync(new WizardPage("Select an audio application",
					"Choose the conference application for the other speaker's audio. At least one audio source is required.", options)
				{
					SelectedKey = "none", Error = error
				}, cancellationToken);
				application = selected == "none" ? null : applications.First(app => app.Id.ToString(CultureInfo.InvariantCulture) == selected);
			}
			if (microphone == null && application == null)
			{
				error = "Select at least one audio source. Close setup to cancel.";
				continue;
			}
			cancellationToken.ThrowIfCancellationRequested();
			SelectedMicrophone = microphone;
			SelectedApplication = application;
			_initialized = true;
			logger.LogInformation("Selected microphone: {Microphone}; audio application: {Application}.",
				microphone?.Name ?? "none", application?.Name ?? "none");
			return;
		}
	}

	internal void Publish(AudioSourceType source, IReadOnlyList<AudioFrame> frames)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (source == AudioSourceType.Microphone)
		{
			MicrophoneRecordingCompleted?.Invoke(frames);
		}
		else if (source == AudioSourceType.Application)
		{
			ApplicationRecordingCompleted?.Invoke(frames);
		}
		else
		{
			throw new ArgumentOutOfRangeException(nameof(source));
		}
	}

	public void Dispose()
	{
		_disposed = true;
		MicrophoneRecordingCompleted = null;
		ApplicationRecordingCompleted = null;
	}
}
