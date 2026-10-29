using aissyst.core;
using aissyst.core.Audio;

namespace aissyst.console;

public sealed class ConsoleCaptureSession(IAudioCapture audioCapture, IApplicationSettings settings,
	ConsoleHotkeySettings hotkeys) : ICaptureSession, IDisposable
{
	private bool _disposed;

	public AudioInputDevice? SelectedMicrophone { get; private set; }
	public AudioApplication? SelectedApplication { get; private set; }
	public bool HasSelectedSources { get; private set; }
	public event Action<IReadOnlyList<AudioFrame>>? MicrophoneRecordingCompleted;
	public event Action<IReadOnlyList<AudioFrame>>? ApplicationRecordingCompleted;

	public async Task InitializeAsync(CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (HasSelectedSources)
		{
			return;
		}
		if (settings.AvailableSources.HasFlag(AudioSourceType.Microphone))
		{
			var (cancelled, microphone) = await SelectAsync(await audioCapture.GetInputDevicesAsync(cancellationToken),
				device => device.Name, "microphone", cancellationToken);
			if (cancelled)
			{
				return;
			}
			SelectedMicrophone = microphone;
		}
		if (settings.AvailableSources.HasFlag(AudioSourceType.Application))
		{
			var (cancelled, application) = await SelectAsync(await audioCapture.GetApplicationsAsync(cancellationToken),
				application => application.Name, "application", cancellationToken);
			if (cancelled)
			{
				return;
			}
			SelectedApplication = application;
		}
		HasSelectedSources = SelectedMicrophone != null || SelectedApplication != null;
		if (!HasSelectedSources)
		{
			Console.WriteLine("No audio sources selected. The application will exit.");
			return;
		}
		if (SelectedMicrophone != null)
		{
			Console.WriteLine($"Selected microphone: {SelectedMicrophone.Name}");
		}
		if (SelectedApplication != null)
		{
			Console.WriteLine($"Selected application: {SelectedApplication.Name}");
		}
	}

	internal void Publish(AudioSourceType source, AudioRecording recording)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		IReadOnlyList<AudioFrame> frames = [new(recording.Samples, recording.Format, TimeSpan.Zero)];
		if (source == AudioSourceType.Microphone)
		{
			MicrophoneRecordingCompleted?.Invoke(frames);
		}
		else
		{
			ApplicationRecordingCompleted?.Invoke(frames);
		}
	}

	private async Task<(bool Cancelled, T? Source)> SelectAsync<T>(IReadOnlyList<T> items, Func<T, string> getName, string source,
		CancellationToken cancellationToken) where T : class
	{
		Console.WriteLine();
		if (items.Count == 0)
		{
			Console.WriteLine($"No available {source}s found.");
		}
		Console.WriteLine($"Available {source}s:");
		Console.WriteLine("0. None");
		for (var index = 0; index < items.Count; index++)
		{
			Console.WriteLine($"{index + 1}. {getName(items[index])}");
		}
		while (true)
		{
			Console.Write($"Select {source} by number (0-{items.Count}), or enter {hotkeys.Quit} to quit: ");
			var input = await ConsoleInput.ReadLineAsync(cancellationToken);
			if (input == null || input.Trim().Equals(hotkeys.Quit.ToString(), StringComparison.OrdinalIgnoreCase))
			{
				return (true, null);
			}
			if (int.TryParse(input, out var number) && number >= 0 && number <= items.Count)
			{
				return (false, number == 0 ? null : items[number - 1]);
			}
			Console.WriteLine($"Invalid selection. Enter a number from 0 to {items.Count}.");
		}
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		MicrophoneRecordingCompleted = null;
		ApplicationRecordingCompleted = null;
	}
}
