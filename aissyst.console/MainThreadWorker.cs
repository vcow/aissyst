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

	public MainThreadWorker(IAudioCapture audioCapture, IServiceScopeFactory scopeFactory,
		IHostApplicationLifetime applicationLifetime, IApplicationSettings settings, ITranscriber transcriber)
	{
		_audioCapture = audioCapture;
		_scopeFactory = scopeFactory;
		_applicationLifetime = applicationLifetime;
		_settings = settings;
		_transcriber = transcriber;
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
				Console.Write("Press Enter to start a session, or enter Q to quit: ");
				var command = await ConsoleInput.ReadLineAsync(stoppingToken);
				if (command == null || command.Trim().Equals("q", StringComparison.OrdinalIgnoreCase))
				{
					return;
				}

				if (!string.IsNullOrWhiteSpace(command))
				{
					Console.WriteLine("Unknown command. Press Enter to start or enter Q to quit.");
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

	private static async Task<T?> SelectAsync<T>(IReadOnlyList<T> items, Func<T, string> getName, string sourceName,
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
			Console.Write($"Select {sourceName} by number (1-{items.Count}), or enter Q to quit: ");
			var input = await ConsoleInput.ReadLineAsync(cancellationToken);
			if (input == null || input.Trim().Equals("q", StringComparison.OrdinalIgnoreCase))
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
			Console.WriteLine(Console.IsInputRedirected
				? "Enter R to finish active recordings, S to stop listening, or Q to quit, then press Enter."
				: "Press R to finish active recordings, S to stop listening, or Q/Ctrl+C to quit.");
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
					if (command == null || command.Equals("q", StringComparison.OrdinalIgnoreCase))
					{
						return true;
					}
					if (command.Equals("s", StringComparison.OrdinalIgnoreCase))
					{
						return false;
					}
					if (command.Equals("r", StringComparison.OrdinalIgnoreCase))
					{
						await session.CompleteRecordingsAsync(stoppingToken);
						continue;
					}

					Console.WriteLine("Unknown command. Use R to finish recordings, S to stop listening, or Q to quit.");
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
}
