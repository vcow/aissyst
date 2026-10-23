using aissyst.core;
using aissyst.core.Audio;
using aissyst.core.Controller;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace aissyst.console;

public class MainThreadWorker : BackgroundService
{
	private readonly ConsoleCaptureSession _captureSession;
	private readonly IServiceScopeFactory _scopeFactory;
	private readonly IHostApplicationLifetime _applicationLifetime;
	private readonly IApplicationSettings _settings;
	private readonly ApplicationController _controller;
	private readonly ConsoleHotkeySettings _hotkeys;

	public MainThreadWorker(ConsoleCaptureSession captureSession, IServiceScopeFactory scopeFactory,
		IHostApplicationLifetime applicationLifetime, IApplicationSettings settings, ApplicationController controller,
		ConsoleHotkeySettings hotkeys)
	{
		_captureSession = captureSession;
		_scopeFactory = scopeFactory;
		_applicationLifetime = applicationLifetime;
		_settings = settings;
		_controller = controller;
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

			WriteHotkeyHelp();
			await _controller.InitializeAsync(stoppingToken);
			if (!_captureSession.HasSelectedSources)
			{
				return;
			}
			while (!stoppingToken.IsCancellationRequested)
			{
				Console.Write($"Press Enter to start a session, enter {_hotkeys.ClearQueue} to clear pending audio and text, or {_hotkeys.Quit} to quit: ");
				var command = await ConsoleInput.ReadLineAsync(stoppingToken);
				if (command == null || Matches(command, _hotkeys.Quit))
				{
					return;
				}
				if (Matches(command, _hotkeys.ClearQueue))
				{
					await ClearQueuesAsync(stoppingToken);
					continue;
				}

				if (!string.IsNullOrWhiteSpace(command))
				{
					Console.WriteLine($"Unknown command. Press Enter to start, enter {_hotkeys.ClearQueue} to clear pending audio and text, or {_hotkeys.Quit} to quit.");
					continue;
				}

				if (await RunSessionAsync(_captureSession.SelectedMicrophone, _captureSession.SelectedApplication, stoppingToken))
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
			try
			{
				await _controller.DisposeAsync();
			}
			finally
			{
				_applicationLifetime.StopApplication();
			}
		}
	}

	// Returns true when the user quits or the input stream closes.
	private async Task<bool> RunSessionAsync(AudioInputDevice? microphone, AudioApplication? application, CancellationToken stoppingToken)
	{
		var sessionStarted = false;
		var processingSessionStarted = false;
		try
		{
			await using var scope = _scopeFactory.CreateAsyncScope();
			var context = scope.ServiceProvider.GetRequiredService<CaptureSessionContext>();
			context.Initialize(microphone, application);
			var session = scope.ServiceProvider.GetRequiredService<CaptureSession>();
			await _controller.BeginSessionAsync(stoppingToken);
			processingSessionStarted = true;
			session.Start();
			sessionStarted = true;

			Console.WriteLine("Session started. Waiting for audio.");
			Console.WriteLine($"{(Console.IsInputRedirected ? "Enter" : "Press")} {KeyName(_hotkeys.FinishRecording)} to send the current fragments and keep listening, "
				+ $"{_hotkeys.StopSession} to stop listening, {_hotkeys.ClearQueue} to clear pending audio and text, or {_hotkeys.Quit} to quit."
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
					var command = await input;
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
						await ClearQueuesAsync(stoppingToken);
						continue;
					}
					if (Matches(command, _hotkeys.FinishRecording))
					{
						await session.CompleteRecordingsAsync(stoppingToken);
						continue;
					}

					Console.WriteLine($"Unknown command. Use {KeyName(_hotkeys.FinishRecording)} to send the current fragments, {_hotkeys.StopSession} to stop listening, "
						+ $"{_hotkeys.ClearQueue} to clear pending audio and text, or {_hotkeys.Quit} to quit.");
				}
			}
			finally
			{
				await inputCancellation.CancelAsync();
			}
		}
		finally
		{
			if (processingSessionStarted)
			{
				Console.WriteLine("Finishing pending transcriptions and saving session text...");
				// Capture has stopped, but the controller must remain active until its queues are drained.
				await _controller.CompleteSessionAsync();
			}
			if (sessionStarted)
			{
				Console.WriteLine("Session stopped.");
			}
		}
	}

	private void WriteHotkeyHelp()
	{
		Console.WriteLine("Hotkeys:");
		Console.WriteLine("  Enter - start a session when not listening.");
		Console.WriteLine($"  {KeyName(_hotkeys.FinishRecording)} - send current audio fragments for transcription and keep listening.");
		Console.WriteLine($"  {_hotkeys.StopSession} - stop listening.");
		Console.WriteLine($"  {_hotkeys.ClearQueue} - clear pending audio and text.");
		Console.WriteLine($"  {_hotkeys.Quit} / Ctrl+C - quit.");
		if (Console.IsInputRedirected)
		{
			Console.WriteLine("Line input: press Enter after each command.");
			if (_hotkeys.FinishRecording == ' ')
			{
				Console.WriteLine("For Space, enter a single space followed by Enter.");
			}
		}
		Console.WriteLine();
	}

	private async Task ClearQueuesAsync(CancellationToken cancellationToken)
	{
		var (records, transcriptions) = await _controller.ClearQueues(cancellationToken);
		Console.WriteLine($"Queues cleared. Removed {records} pending audio recording(s) and {transcriptions} pending transcription(s).");
	}

	private static bool Matches(string input, char hotkey)
	{
		return hotkey == ' '
			? input == " "
			: input.Trim().Equals(hotkey.ToString(), StringComparison.OrdinalIgnoreCase);
	}

	private static string KeyName(char hotkey) => hotkey == ' ' ? "Space" : hotkey.ToString();
}
