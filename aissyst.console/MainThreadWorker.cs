using aissyst.core.Audio;
using Microsoft.Extensions.Hosting;

namespace aissyst.console;

public class MainThreadWorker : BackgroundService
{
	private readonly IAudioCapture _audioCapture;

	public MainThreadWorker(IAudioCapture audioCapture)
	{
		_audioCapture = audioCapture;
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		if (!OperatingSystem.IsMacOS())
		{
			Console.WriteLine("Microphone enumeration is currently implemented only for macOS.");
			return;
		}

		try
		{
			var devices = await _audioCapture.GetInputDevicesAsync(stoppingToken);
			if (devices.Count == 0)
			{
				Console.WriteLine("No available microphones found.");
				return;
			}

			Console.WriteLine("Available microphones (audio input devices):");
			for (var index = 0; index < devices.Count; index++)
			{
				var device = devices[index];
				var defaultLabel = device.isDefault ? " [default]" : string.Empty;
				Console.WriteLine($"{index + 1}. {device.Name}{defaultLabel}");
				Console.WriteLine($"   UID: {device.Id}");
				Console.WriteLine($"   Input channels: {device.Channels}");
			}
		}
		catch (InvalidOperationException exception)
		{
			Console.Error.WriteLine($"Failed to enumerate microphones: {exception.Message}");
			Environment.ExitCode = 1;
		}
	}
}