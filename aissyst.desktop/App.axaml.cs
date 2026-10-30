using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using aissyst.core.Controller;
using aissyst.desktop.ViewModels;
using aissyst.desktop.Views;
using aissyst.desktop.Wizard;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace aissyst.desktop;

public partial class App : Application
{
	private readonly CancellationTokenSource _shutdown = new();
	private ServiceProvider? _services;
	private Task? _startup;
	private bool _stopping;
	private bool _shutdownFinished;

	public override void Initialize() => AvaloniaXamlLoader.Load(this);

	public override void OnFrameworkInitializationCompleted()
	{
		if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
		{
			var log = new LogViewModel();
			var main = new MainWindow { DataContext = log };
			_services = DesktopServices.Create(main, log).BuildServiceProvider(
				new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
			desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
			desktop.MainWindow = main;
			main.Opened += (_, _) => Dispatcher.UIThread.Post(() =>
			{
				if (!_stopping)
				{
					_startup = InitializeAsync(_services);
				}
			}, DispatcherPriority.ApplicationIdle);
			main.Closing += (_, args) =>
			{
				if (!_shutdownFinished)
				{
					args.Cancel = true;
					BeginShutdown(desktop);
				}
			};
			desktop.ShutdownRequested += (_, args) =>
			{
				if (!_shutdownFinished)
				{
					args.Cancel = true;
					BeginShutdown(desktop);
				}
			};
		}
		base.OnFrameworkInitializationCompleted();
	}

	private async Task InitializeAsync(ServiceProvider services)
	{
		var logger = services.GetRequiredService<ILogger<App>>();
		var wizard = (WizardService)services.GetRequiredService<IWizardService>();
		using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, wizard.CancellationToken);
		try
		{
			logger.LogInformation("Desktop application started. Complete setup to connect to the LLM.");
			await Task.Run(() => services.GetRequiredService<ApplicationController>().InitializeAsync(cancellation.Token), cancellation.Token);
			cancellation.Token.ThrowIfCancellationRequested();
			logger.LogInformation("Setup completed. The LLM session is ready. Audio sources are configured; capture has not started.");
			await wizard.CompleteAsync();
		}
		catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
		{
			logger.LogInformation("Setup cancelled. Close the log window to exit.");
		}
		catch (Exception exception)
		{
			logger.LogError(exception, "Startup failed. Close the application and restart to try again.");
			await wizard.FailAsync($"Setup failed: {exception.Message}\nSee the log window for details. Close the application to exit.");
		}
	}

	private async void BeginShutdown(IClassicDesktopStyleApplicationLifetime desktop)
	{
		if (_stopping)
		{
			return;
		}
		_stopping = true;
		var exitCode = 0;
		try
		{
			await _shutdown.CancelAsync();
			if (_startup != null)
			{
				await _startup;
			}
			if (_services != null)
			{
				await _services.DisposeAsync();
			}
		}
		catch (Exception exception)
		{
			exitCode = 1;
			System.Diagnostics.Trace.WriteLine(exception);
		}
		finally
		{
			_shutdown.Dispose();
			_shutdownFinished = true;
			desktop.Shutdown(exitCode);
		}
	}
}
