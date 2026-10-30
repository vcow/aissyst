using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using aissyst.desktop.Settings;
using aissyst.desktop.Views;
using Microsoft.Extensions.Logging;

namespace aissyst.desktop;

internal sealed class DesktopApplicationShell
{
	private readonly MainWindow _log;
	private readonly DesktopSettingsDialog _settings;
	private readonly ILogger<DesktopApplicationShell> _logger;
	private bool _allowClose;
	private bool _shuttingDown;

	public DesktopApplicationShell(Application application, MainWindow log, DesktopSettingsDialog settings,
		Action quit, ILogger<DesktopApplicationShell> logger)
	{
		_log = log;
		_settings = settings;
		_logger = logger;
		// macOS exports the application menu before this shell is created.
		// Keep that menu instance so its native exporter observes the changes.
		NativeMenu.SetMenu(application, CreateMenu(quit, NativeMenu.GetMenu(application)));
		NativeDock.SetMenu(application, CreateMenu(quit));
		log.SettingsRequested += OpenSettings;
		log.QuitRequested += quit;
		log.Closing += (_, args) =>
		{
			if (!_allowClose)
			{
				args.Cancel = true;
				if (OperatingSystem.IsMacOS())
				{
					log.Hide();
				}
				else
				{
					log.WindowState = WindowState.Minimized;
				}
			}
		};
	}

	private NativeMenu CreateMenu(Action quit, NativeMenu? menu = null)
	{
		var settings = new NativeMenuItem("Settings…") { Gesture = new KeyGesture(Key.OemComma, KeyModifiers.Meta) };
		settings.Click += (_, _) => OpenSettings();
		var showLog = new NativeMenuItem("Show Log");
		showLog.Click += (_, _) => ShowLog();
		var exit = new NativeMenuItem("Quit") { Gesture = new KeyGesture(Key.Q, KeyModifiers.Meta) };
		exit.Click += (_, _) => quit();
		menu ??= new NativeMenu();
		menu.Items.Clear();
		menu.Items.Add(settings);
		menu.Items.Add(showLog);
		menu.Items.Add(new NativeMenuItemSeparator());
		menu.Items.Add(exit);
		return menu;
	}

	public void ShowLog()
	{
		if (_shuttingDown)
		{
			return;
		}
		_log.Show();
		if (_log.WindowState == WindowState.Minimized)
		{
			_log.WindowState = WindowState.Normal;
		}
		_log.Activate();
	}

	public void AllowClose() => _allowClose = true;
	public void PrepareShutdown() => _shuttingDown = true;

	private async void OpenSettings()
	{
		if (_shuttingDown)
		{
			return;
		}
		try
		{
			await _settings.ShowAsync();
		}
		catch (Exception exception)
		{
			_logger.LogError(exception, "Failed to open settings.");
		}
	}
}
