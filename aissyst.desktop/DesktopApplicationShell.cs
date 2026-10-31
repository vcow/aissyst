using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using aissyst.desktop.Settings;
using aissyst.desktop.Dialog;
using aissyst.desktop.Views;
using Microsoft.Extensions.Logging;

namespace aissyst.desktop;

internal sealed class DesktopApplicationShell
{
	private readonly MainWindow _log;
	private readonly DesktopSettingsDialog _settings;
	private readonly ILogger<DesktopApplicationShell> _logger;
	private readonly DesktopDialogService? _dialog;
	private readonly IDisposable _openedMenus;
	private bool _allowClose;
	private bool _shuttingDown;

	public DesktopApplicationShell(Application application, MainWindow log, DesktopSettingsDialog settings,
		Action quit, ILogger<DesktopApplicationShell> logger, DesktopDialogService? dialog = null)
	{
		_log = log;
		_settings = settings;
		_logger = logger;
		_dialog = dialog;
		// macOS exports the application menu before this shell is created.
		// Keep that menu instance so its native exporter observes the changes.
		NativeMenu.SetMenu(application, CreateMenu(quit, NativeMenu.GetMenu(application)));
		NativeDock.SetMenu(application, CreateMenu(quit));
		InstallInterviewMenu(log);
		_openedMenus = Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) => InstallInterviewMenu(window));
		log.SettingsRequested += OpenSettings;
		log.QuitRequested += quit;
		log.ShowDialogRequested += ShowDialog;
		log.SaveDialogRequested += SaveDialog;
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

	private void InstallInterviewMenu(Window window)
	{
		var showDialog = new NativeMenuItem("Show Dialog");
		showDialog.Click += (_, _) => ShowDialog();
		var saveDialog = new NativeMenuItem("Save Dialog…");
		saveDialog.Click += (_, _) => SaveDialog();
		var interview = new NativeMenu();
		interview.Items.Add(showDialog);
		interview.Items.Add(saveDialog);
		interview.NeedsUpdate += (_, _) =>
		{
			showDialog.IsEnabled = saveDialog.IsEnabled = !_shuttingDown && _dialog != null && _settings.Window == null;
		};
		var menu = NativeMenu.GetMenu(window) ?? new NativeMenu();
		menu.Items.Clear();
		menu.Items.Add(new NativeMenuItem("Interview") { Menu = interview });
		NativeMenu.SetMenu(window, menu);
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

	public void AllowClose()
	{
		_allowClose = true;
		_openedMenus.Dispose();
	}
	public void PrepareShutdown() => _shuttingDown = true;

	public void ShowDialog()
	{
		if (!_shuttingDown && _settings.Window == null)
		{
			_dialog?.Show();
		}
	}

	private async void SaveDialog()
	{
		if (_shuttingDown || _settings.Window != null || _dialog == null)
		{
			return;
		}
		try
		{
			await _dialog.SaveAsync();
		}
		catch (OperationCanceledException) when (_shuttingDown)
		{
		}
		catch (Exception exception)
		{
			_logger.LogError(exception, "Failed to save the dialog.");
			ShowLog();
		}
	}

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
