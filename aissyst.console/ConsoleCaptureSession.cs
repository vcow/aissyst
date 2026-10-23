using aissyst.core;
using aissyst.core.Audio;
using R3;

namespace aissyst.console;

public sealed class ConsoleCaptureSession(IAudioCapture audioCapture, IApplicationSettings settings,
	ConsoleHotkeySettings hotkeys) : ICaptureSession, IDisposable
{
	private readonly Subject<IReadOnlyList<AudioFrame>> _microphoneRecords = new();
	private readonly Subject<IReadOnlyList<AudioFrame>> _applicationRecords = new();
	private bool _disposed;

	public AudioInputDevice? SelectedMicrophone { get; private set; }
	public AudioApplication? SelectedApplication { get; private set; }
	public bool HasSelectedSources { get; private set; }
	public Observable<IReadOnlyList<AudioFrame>> MicrophoneRecordObservable => _microphoneRecords;
	public Observable<IReadOnlyList<AudioFrame>> ApplicationRecordingObservable => _applicationRecords;

	public async Task InitializeAsync(CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (HasSelectedSources)
		{
			return;
		}
		if (settings.AvailableSources.HasFlag(AudioSourceType.Microphone))
		{
			SelectedMicrophone = await SelectAsync(await audioCapture.GetInputDevicesAsync(cancellationToken),
				device => device.Name, "microphone", cancellationToken);
			if (SelectedMicrophone == null)
			{
				return;
			}
		}
		if (settings.AvailableSources.HasFlag(AudioSourceType.Application))
		{
			SelectedApplication = await SelectAsync(await audioCapture.GetApplicationsAsync(cancellationToken),
				application => application.Name, "application", cancellationToken);
			if (SelectedApplication == null)
			{
				return;
			}
		}
		HasSelectedSources = SelectedMicrophone != null || SelectedApplication != null;
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
			_microphoneRecords.OnNext(frames);
		}
		else
		{
			_applicationRecords.OnNext(frames);
		}
	}

	private async Task<T?> SelectAsync<T>(IReadOnlyList<T> items, Func<T, string> getName, string source,
		CancellationToken cancellationToken) where T : class
	{
		Console.WriteLine();
		if (items.Count == 0)
		{
			Console.WriteLine($"No available {source}s found.");
			return null;
		}
		Console.WriteLine($"Available {source}s:");
		for (var index = 0; index < items.Count; index++)
		{
			Console.WriteLine($"{index + 1}. {getName(items[index])}");
		}
		while (true)
		{
			Console.Write($"Select {source} by number (1-{items.Count}), or enter {hotkeys.Quit} to quit: ");
			var input = await ConsoleInput.ReadLineAsync(cancellationToken);
			if (input == null || input.Trim().Equals(hotkeys.Quit.ToString(), StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}
			if (int.TryParse(input, out var number) && number >= 1 && number <= items.Count)
			{
				return items[number - 1];
			}
			Console.WriteLine($"Invalid selection. Enter a number from 1 to {items.Count}.");
		}
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		_microphoneRecords.Dispose();
		_applicationRecords.Dispose();
	}
}
