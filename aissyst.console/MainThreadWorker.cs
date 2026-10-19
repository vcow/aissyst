using aissyst.core.Audio;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace aissyst.console;

public class MainThreadWorker : BackgroundService
{
	private readonly IAudioCapture _audioCapture;
	private readonly IServiceScopeFactory _scopeFactory;
	private readonly IHostApplicationLifetime _applicationLifetime;

	public MainThreadWorker(IAudioCapture audioCapture, IServiceScopeFactory scopeFactory,
		IHostApplicationLifetime applicationLifetime)
	{
		_audioCapture = audioCapture;
		_scopeFactory = scopeFactory;
		_applicationLifetime = applicationLifetime;
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		try
		{
			if (!OperatingSystem.IsMacOS())
			{
				Console.WriteLine("Microphone and application selection is currently implemented only for macOS.");
				return;
			}

			var devices = await _audioCapture.GetInputDevicesAsync(stoppingToken);
			var microphone = await SelectAsync(devices, device => device.Name, "microphone", stoppingToken);
			if (microphone == null)
			{
				return;
			}

			var applications = await _audioCapture.GetApplicationsAsync(stoppingToken);
			var application = await SelectAsync(applications, app => app.Name, "application", stoppingToken);
			if (application == null)
			{
				return;
			}

			Console.WriteLine();
			Console.WriteLine($"Selected microphone: {microphone.Name}");
			Console.WriteLine($"Selected application: {application.Name}");
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
		catch (Exception exception) when (exception is InvalidOperationException or PlatformNotSupportedException)
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
			Console.WriteLine($"{index + 1}. {getName(items[index])}");

		while (true)
		{
			Console.Write($"Select {sourceName} by number (1-{items.Count}), or enter Q to quit: ");
			var input = await ConsoleInput.ReadLineAsync(cancellationToken);
			if (input == null || input.Trim().Equals("q", StringComparison.OrdinalIgnoreCase))
				return null;
			if (int.TryParse(input, out var number) && number >= 1 && number <= items.Count)
				return items[number - 1];

			Console.WriteLine($"Invalid selection. Enter a number from 1 to {items.Count}.");
		}
	}

	// Returns true when the user quits or the input stream closes.
	private async Task<bool> RunSessionAsync(AudioInputDevice microphone, AudioApplication application, CancellationToken stoppingToken)
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

			Console.WriteLine("Session started (placeholder).");
			Console.WriteLine(Console.IsInputRedirected
				? "Enter S to stop the session or Q to quit, then press Enter. Ctrl+C also quits."
				: "Press S to stop the session, Q to quit, or Ctrl+C to quit.");
			while (true)
			{
				var command = (await ConsoleInput.ReadCommandAsync(stoppingToken))?.Trim();
				if (command == null || command.Equals("q", StringComparison.OrdinalIgnoreCase))
					return true;
				if (command.Equals("s", StringComparison.OrdinalIgnoreCase))
					return false;

				Console.WriteLine("Unknown command. Use S to stop the session or Q to quit.");
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