using aissyst.core;
using aissyst.core.Audio;
using aissyst.core.Transcription;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace aissyst.console;

public class MainThreadWorker : BackgroundService
{
	private readonly IAudioCapture _audioCapture;
	private readonly IServiceScopeFactory _scopeFactory;
	private readonly IHostApplicationLifetime _applicationLifetime;
	private readonly IApplicationSettings _settings;
	private readonly ITranscriber _transcriber;
	private readonly TranscriptionQueue _transcriptionQueue;
	private readonly ConsoleHotkeySettings _hotkeys;

	public MainThreadWorker(IAudioCapture audioCapture, IServiceScopeFactory scopeFactory,
		IHostApplicationLifetime applicationLifetime, IApplicationSettings settings, ITranscriber transcriber,
		TranscriptionQueue transcriptionQueue, ConsoleHotkeySettings hotkeys)
	{
		_audioCapture = audioCapture;
		_scopeFactory = scopeFactory;
		_applicationLifetime = applicationLifetime;
		_settings = settings;
		_transcriber = transcriber;
		_transcriptionQueue = transcriptionQueue;
		_hotkeys = hotkeys;
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		try
		{
			var sources = _settings.AvailableSources;
			if (sources == AudioSourceType.None)
			{
				Console.WriteLine("No audio sources are enabled. The application will exit.");
				return;
			}
			if ((sources & ~AudioSourceType.Both) != AudioSourceType.None)
			{
				throw new InvalidOperationException("The configured audio sources are not supported.");
			}

			await _transcriber.InitializeAsync(stoppingToken);

			AudioInputDevice? microphone = null;
			AudioApplication? application = null;
			if (sources.HasFlag(AudioSourceType.Microphone))
			{
				var devices = await _audioCapture.GetInputDevicesAsync(stoppingToken);
				microphone = await SelectAsync(devices, device => device.Name, "microphone", stoppingToken);
				if (microphone == null)
				{
					return;
				}
			}
			if (sources.HasFlag(AudioSourceType.Application))
			{
				var applications = await _audioCapture.GetApplicationsAsync(stoppingToken);
				application = await SelectAsync(applications, app => app.Name, "application", stoppingToken);
				if (application == null)
				{
					return;
				}
			}

			Console.WriteLine();
			if (microphone != null)
			{
				Console.WriteLine($"Selected microphone: {microphone.Name}");
			}
			if (application != null)
			{
				Console.WriteLine($"Selected application: {application.Name}");
			}
			while (!stoppingToken.IsCancellationRequested)
			{
				Console.Write($"Press Enter to start a session, enter {_hotkeys.ClearQueue} to clear the transcription queue, or {_hotkeys.Quit} to quit: ");
				var command = await ConsoleInput.ReadLineAsync(stoppingToken);
				if (command == null || Matches(command, _hotkeys.Quit))
				{
					return;
				}
				if (Matches(command, _hotkeys.ClearQueue))
				{
					ClearTranscriptionQueue();
					continue;
				}

				if (!string.IsNullOrWhiteSpace(command))
				{
					Console.WriteLine($"Unknown command. Press Enter to start, enter {_hotkeys.ClearQueue} to clear the queue, or {_hotkeys.Quit} to quit.");
					continue;
				}

				if (await RunSessionAsync(microphone, application, stoppingToken))
				{
					return;
				}
			}
		}
		catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
		{
			// Console input and the active session stop with the host.
		}
		catch (Exception exception) when (exception is InvalidOperationException or PlatformNotSupportedException
			or IOException or UnauthorizedAccessException)
		{
			await Console.Error.WriteLineAsync($"Failed to run the console workflow: {exception.Message}");
			Environment.ExitCode = 1;
		}
		finally
		{
			_applicationLifetime.StopApplication();
		}
	}

	private async Task<T?> SelectAsync<T>(IReadOnlyList<T> items, Func<T, string> getName, string sourceName,
		CancellationToken cancellationToken) where T : class
	{
		Console.WriteLine();
		if (items.Count == 0)
		{
			Console.WriteLine($"No available {sourceName}s found.");
			return null;
		}

		Console.WriteLine($"Available {sourceName}s:");
		for (var index = 0; index < items.Count; index++)
		{
			Console.WriteLine($"{index + 1}. {getName(items[index])}");
		}

		while (true)
		{
			Console.Write($"Select {sourceName} by number (1-{items.Count}), enter {_hotkeys.ClearQueue} to clear the queue, or {_hotkeys.Quit} to quit: ");
			var input = await ConsoleInput.ReadLineAsync(cancellationToken);
			if (input == null || Matches(input, _hotkeys.Quit))
			{
				return null;
			}
			if (Matches(input, _hotkeys.ClearQueue))
			{
				ClearTranscriptionQueue();
				continue;
			}
			if (int.TryParse(input, out var number) && number >= 1 && number <= items.Count)
			{
				return items[number - 1];
			}

			Console.WriteLine($"Invalid selection. Enter a number from 1 to {items.Count}.");
		}
	}

	// Returns true when the user quits or the input stream closes.
	private async Task<bool> RunSessionAsync(AudioInputDevice? microphone, AudioApplication? application, CancellationToken stoppingToken)
	{
		var sessionStarted = false;
		try
		{
			await using var scope = _scopeFactory.CreateAsyncScope();
			var context = scope.ServiceProvider.GetRequiredService<CaptureSessionContext>();
			context.Initialize(microphone, application);
			var session = scope.ServiceProvider.GetRequiredService<CaptureSession>();
			session.Start();
			sessionStarted = true;

			Console.WriteLine("Session started. Waiting for audio.");
			Console.WriteLine($"{(Console.IsInputRedirected ? "Enter" : "Press")} {_hotkeys.FinishRecording} to finish active recordings, "
				+ $"{_hotkeys.StopSession} to stop listening, {_hotkeys.ClearQueue} to clear the transcription queue, or {_hotkeys.Quit} to quit."
				+ (Console.IsInputRedirected ? " Then press Enter." : " Ctrl+C also quits."));
			using var inputCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
			try
			{
				while (true)
				{
					var input = ConsoleInput.ReadCommandAsync(inputCancellation.Token);
					if (await Task.WhenAny(input, session.Completion) == session.Completion)
					{
						await session.Completion;
						return true;
					}
					var command = (await input)?.Trim();
					if (command == null || Matches(command, _hotkeys.Quit))
					{
						return true;
					}
					if (Matches(command, _hotkeys.StopSession))
					{
						return false;
					}
					if (Matches(command, _hotkeys.ClearQueue))
					{
						ClearTranscriptionQueue();
						continue;
					}
					if (Matches(command, _hotkeys.FinishRecording))
					{
						await session.CompleteRecordingsAsync(stoppingToken);
						continue;
					}

					Console.WriteLine($"Unknown command. Use {_hotkeys.FinishRecording} to finish recordings, {_hotkeys.StopSession} to stop listening, "
						+ $"{_hotkeys.ClearQueue} to clear the queue, or {_hotkeys.Quit} to quit.");
				}
			}
			finally
			{
				await inputCancellation.CancelAsync();
			}
		}
		finally
		{
			if (sessionStarted)
			{
				Console.WriteLine("Session stopped.");
			}
		}
	}

	private void ClearTranscriptionQueue()
	{
		var removed = _transcriptionQueue.Clear();
		Console.WriteLine($"Transcription queue cleared. Removed {removed} pending recording(s).");
	}

	private static bool Matches(string input, char hotkey)
	{
		return input.Trim().Equals(hotkey.ToString(), StringComparison.OrdinalIgnoreCase);
	}
}
