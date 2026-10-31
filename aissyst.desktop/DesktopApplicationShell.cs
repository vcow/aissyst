using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using aissyst.desktop.Settings;
using aissyst.desktop.Dialog;
using aissyst.desktop.Interview;
using aissyst.desktop.State;
using aissyst.desktop.Views;
using Microsoft.Extensions.Logging;

namespace aissyst.desktop;

internal sealed class DesktopApplicationShell
{
	private readonly Application _application;
	private readonly MainWindow _log;
	private readonly DesktopSettingsDialog _settings;
	private readonly ILogger<DesktopApplicationShell> _logger;
	private readonly DesktopDialogService? _dialog;
	private readonly DesktopInterview? _interview;
	private readonly InterviewCommands? _interviewCommands;
	private readonly DesktopPromptService? _prompt;
	private readonly IDisposable _openedMenus;
	private readonly IDisposable _visibilityChanges;
	private readonly IDisposable _windowStateChanges;
	private readonly DesktopWindowState? _windowState;
	private readonly CancellationTokenSource _commands = new();
	private readonly List<(Window? Owner, Action Update)> _menuStates = [];
	private readonly HashSet<Window> _menuWindows = [];
	private readonly Action _quit;
	private Task? _reconnectTask;
	private Task? _saveTask;
	private Task? _settingsTask;
	private bool _allowClose;
	private bool _shuttingDown;
	internal ConfirmationWindow? Confirmation { get; private set; }
	private bool HasModal => _settings.Window != null || Confirmation != null;
	private bool CanUseWindows => !_shuttingDown && !HasModal;

	public DesktopApplicationShell(Application application, MainWindow log, DesktopSettingsDialog settings,
		Action quit, ILogger<DesktopApplicationShell> logger, DesktopDialogService? dialog = null,
		DesktopWindowState? windowState = null, DesktopInterview? interview = null,
		InterviewCommands? interviewCommands = null, DesktopPromptService? prompt = null)
	{
		_application = application;
		_log = log;
		_settings = settings;
		_logger = logger;
		_dialog = dialog;
		_interview = interview;
		_interviewCommands = interviewCommands;
		_prompt = prompt;
		_windowState = windowState;
		_quit = quit;
		if (interviewCommands != null)
		{
			interviewCommands.Changed += UpdateMenus;
			interviewCommands.Executing += CommandExecuting;
		}
		// The macOS exporter captures this root before the shell is created.
		var applicationMenu = CreateApplicationMenu(NativeMenu.GetMenu(application));
		if (applicationMenu.Parent is { } applicationItem)
		{
			applicationItem.Header = "Aisyst";
		}
		NativeMenu.SetMenu(application, applicationMenu);
		NativeDock.SetMenu(application, CreateFullMenu(null, true));
		InstallMenus(log);
		_openedMenus = Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) => InstallMenus(window));
		_visibilityChanges = Visual.IsVisibleProperty.Changed.AddClassHandler<Window>((_, _) => UpdateMenus());
		_windowStateChanges = Window.WindowStateProperty.Changed.AddClassHandler<Window>((_, _) => UpdateMenus());
		if (_interview != null)
		{
			_interview.StateChanged += InterviewStateChanged;
		}
		log.Closing += (_, args) =>
		{
			if (!_allowClose)
			{
				args.Cancel = true;
				log.Hide();
			}
		};
	}

	public void OpenStartupWindows()
	{
		_windowState?.Attach(_log, "log");
		if (_windowState?.ShouldShow("log") ?? true)
		{
			_log.Show();
		}
		_dialog?.OpenAtStartup();
		_prompt?.OpenAtStartup();
		UpdateMenus();
	}

	private void InstallMenus(Window window)
	{
		if (_menuWindows.Add(window))
		{
			window.AddHandler(InputElement.KeyDownEvent, HandleShortcut, RoutingStrategies.Bubble);
			NativeMenu.SetMenu(window, CreateFullMenu(window, !OperatingSystem.IsMacOS(), NativeMenu.GetMenu(window)));
			window.Closed += (_, _) =>
			{
				_menuWindows.Remove(window);
				window.RemoveHandler(InputElement.KeyDownEvent, HandleShortcut);
				_menuStates.RemoveAll(state => state.Owner == window);
			};
		}
		UpdateMenus();
	}

	private NativeMenu CreateApplicationMenu(NativeMenu? menu = null, Window? owner = null)
	{
		menu ??= new NativeMenu();
		menu.Items.Clear();
		var settings = Item("Settings…", OpenSettings);
		settings.Gesture = new KeyGesture(Key.OemComma, KeyModifiers.Meta);
		var quit = Item("Quit", _quit);
		quit.Gesture = new KeyGesture(Key.Q, KeyModifiers.Meta);
		menu.Items.Add(settings);
		menu.Items.Add(new NativeMenuItemSeparator());
		menu.Items.Add(quit);
		Register(menu, owner, () =>
		{
			settings.IsEnabled = !_shuttingDown && Confirmation == null;
			quit.IsEnabled = !_shuttingDown;
		});
		return menu;
	}

	private NativeMenu CreateFullMenu(Window? owner, bool includeApplication, NativeMenu? menu = null)
	{
		menu ??= new NativeMenu();
		menu.Items.Clear();
		if (includeApplication)
		{
			menu.Items.Add(new NativeMenuItem("Aisyst") { Menu = CreateApplicationMenu(owner: owner) });
		}
		menu.Items.Add(new NativeMenuItem("Interview") { Menu = CreateInterviewMenu(owner) });
		menu.Items.Add(new NativeMenuItem("Windows") { Menu = CreateWindowsMenu(owner) });
		return menu;
	}

	private NativeMenu CreateInterviewMenu(Window? owner)
	{
		var menu = new NativeMenu();
		var commandItems = new List<(NativeMenuItem Item, InterviewCommand Command)>();
		if (_interviewCommands != null)
		{
			foreach (var command in _interviewCommands.Items)
			{
				var item = new NativeMenuItem(command.Caption) { Command = command, Gesture = command.Gesture };
				menu.Items.Add(item);
				commandItems.Add((item, command));
			}
		}
		else
		{
			foreach (var caption in new[] { "Force Dialog", "Translate", "Suggest", "Answer", "Translate & Answer", "Translate & Suggest" })
			{
				menu.Items.Add(new NativeMenuItem(caption) { IsEnabled = false });
			}
		}
		menu.Items.Add(new NativeMenuItemSeparator());
		var reconnect = Item("Reconnect…", Reconnect);
		menu.Items.Add(reconnect);
		var save = Item("Save Dialog…", SaveDialog);
		menu.Items.Add(save);
		Register(menu, owner, () =>
		{
			foreach (var (item, command) in commandItems)
			{
				item.Gesture = command.Gesture;
				item.IsEnabled = CanUseWindows && command.CanExecute(null);
			}
			reconnect.IsEnabled = CanUseWindows && _interview?.IsInitialized == true && _reconnectTask is not { IsCompleted: false };
			save.IsEnabled = CanUseWindows && _dialog != null && _saveTask is not { IsCompleted: false };
		});
		return menu;
	}

	private NativeMenu CreateWindowsMenu(Window? owner)
	{
		var menu = new NativeMenu();
		var log = Item("Log", () =>
		{
			if (CanUseWindows)
			{
				if (IsShown(_log)) { _log.Hide(); }
				else { ShowLog(); }
			}
		});
		var dialog = Item("Dialog", () =>
		{
			if (CanUseWindows && _dialog != null)
			{
				if (IsShown(_dialog.Window)) { _dialog.Window!.Hide(); }
				else { ShowDialog(); }
			}
		});
		log.ToggleType = dialog.ToggleType = MenuItemToggleType.CheckBox;
		menu.Items.Add(log);
		menu.Items.Add(dialog);
		var prompt = Item("Prompt", () =>
		{
			if (CanUseWindows && _prompt != null)
			{
				if (IsShown(_prompt.Window)) { _prompt.Window!.Hide(); }
				else { _prompt.Show(); }
				UpdateMenus();
			}
		});
		prompt.ToggleType = MenuItemToggleType.CheckBox;
		menu.Items.Add(prompt);
		menu.Items.Add(new NativeMenuItemSeparator());
		var fit = Item("Fit to Screen", () =>
		{
			if (CanUseWindows)
			{
				_windowState?.FitToScreens();
			}
		});
		menu.Items.Add(fit);
		Register(menu, owner, () =>
		{
			log.IsChecked = IsShown(_log);
			dialog.IsChecked = IsShown(_dialog?.Window);
			prompt.IsChecked = IsShown(_prompt?.Window);
			prompt.IsEnabled = CanUseWindows && _prompt != null;
			log.IsEnabled = CanUseWindows;
			dialog.IsEnabled = CanUseWindows && _dialog != null;
			fit.IsEnabled = CanUseWindows && _windowState != null;
		});
		return menu;
	}

	private static bool IsShown(Window? window) => window is { IsVisible: true, WindowState: not WindowState.Minimized };
	private static NativeMenuItem Item(string caption, Action action)
	{
		var item = new NativeMenuItem(caption);
		item.Click += (_, _) => action();
		return item;
	}
	private void Register(NativeMenu menu, Window? owner, Action update)
	{
		_menuStates.Add((owner, update));
		menu.NeedsUpdate += (_, _) => update();
		update();
	}
	private void UpdateMenus()
	{
		_interviewCommands?.SetBlocked(!CanUseWindows);
		foreach (var state in _menuStates.ToArray())
		{
			state.Update();
		}
	}
	private void InterviewStateChanged() => Dispatcher.UIThread.Post(UpdateMenus);

	private void CommandExecuting(InterviewCommand command)
	{
		if (command.Request == null) { _dialog?.Show(activate: false); }
	}

	private void HandleShortcut(object? sender, KeyEventArgs args)
	{
		if (args.Handled || !CanUseWindows || sender is not Window { IsActive: true, IsEnabled: true } window
			|| _interviewCommands == null)
		{
			return;
		}
		// Cocoa consumes native menu accelerators before routing keyboard events to the window.
		if (OperatingSystem.IsMacOS() && NativeMenu.GetIsNativeMenuExported(window)) { return; }
		var command = _interviewCommands.Items.FirstOrDefault(item => item.Gesture?.Matches(args) == true);
		if (command?.CanExecute(null) == true)
		{
			args.Handled = true;
			command.Execute(null);
		}
	}

	public void ShowLog()
	{
		if (_shuttingDown || HasModal)
		{
			return;
		}
		_log.Show();
		if (_log.WindowState == WindowState.Minimized)
		{
			_log.WindowState = WindowState.Normal;
		}
		_log.Activate();
		UpdateMenus();
	}

	public void ShowDialog()
	{
		if (CanUseWindows)
		{
			_dialog?.Show();
			UpdateMenus();
		}
	}

	private void Reconnect()
	{
		if (!CanUseWindows || _interview?.IsInitialized != true || _reconnectTask is { IsCompleted: false })
		{
			return;
		}
		_reconnectTask = RunCommandAsync(ReconnectCoreAsync, "Failed to reconnect to the LLM.");
		UpdateMenus();
	}

	private async Task ReconnectCoreAsync()
	{
		var windows = (_application.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows
			?? _menuWindows.ToArray();
		var owner = windows.FirstOrDefault(window => window.IsVisible && window.IsActive)
			?? windows.FirstOrDefault(window => window.IsVisible);
		if (owner == null)
		{
			ShowLog();
			owner = _log;
		}
		Confirmation = new ConfirmationWindow();
		var confirmation = Confirmation;
		var disabled = new HashSet<Window>();
		void Disable(Window window)
		{
			if (window != confirmation && window.IsEnabled)
			{
				disabled.Add(window);
				window.IsEnabled = false;
			}
		}
		using var opened = Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) => Disable(window));
		bool confirmed;
		try
		{
			foreach (var window in windows)
			{
				Disable(window);
			}
			UpdateMenus();
			confirmed = await confirmation.ShowDialog<bool>(owner);
		}
		finally
		{
			Confirmation = null;
			foreach (var window in disabled)
			{
				window.IsEnabled = true;
			}
			UpdateMenus();
		}
		_commands.Token.ThrowIfCancellationRequested();
		if (confirmed)
		{
			await _interview!.ReconnectAsync(_commands.Token);
			_logger.LogInformation("Reconnected to the LLM with the current session and dialog.");
		}
	}

	private void SaveDialog()
	{
		if (!CanUseWindows || _dialog == null || _saveTask is { IsCompleted: false })
		{
			return;
		}
		_saveTask = RunCommandAsync(_dialog.SaveAsync, "Failed to save the dialog.");
		UpdateMenus();
	}
	private void OpenSettings()
	{
		if (_shuttingDown || Confirmation != null)
		{
			return;
		}
		_settingsTask = RunCommandAsync(_settings.ShowAsync, "Failed to open settings.");
		UpdateMenus();
	}
	private async Task RunCommandAsync(Func<Task> action, string error)
	{
		try
		{
			await action();
		}
		catch (OperationCanceledException) when (_commands.IsCancellationRequested)
		{
		}
		catch (Exception exception)
		{
			_logger.LogError(exception, "{Message}", error);
		}
		finally
		{
			Dispatcher.UIThread.Post(UpdateMenus);
		}
	}

	public void PrepareShutdown()
	{
		_shuttingDown = true;
		_windowState?.StopTracking();
		_commands.Cancel();
		_interviewCommands?.Stop();
		Confirmation?.Close(false);
		_settings.Close();
		UpdateMenus();
	}
	public Task WaitForCommandsAsync() => Task.WhenAll(new[] { _reconnectTask, _settingsTask, _interviewCommands?.WaitForIdleAsync() }
		.Where(task => task != null).Cast<Task>());
	public void AllowClose()
	{
		_allowClose = true;
		_openedMenus.Dispose();
		_visibilityChanges.Dispose();
		_windowStateChanges.Dispose();
		foreach (var window in _menuWindows)
		{
			window.RemoveHandler(InputElement.KeyDownEvent, HandleShortcut);
		}
		if (_interviewCommands != null)
		{
			_interviewCommands.Changed -= UpdateMenus;
			_interviewCommands.Executing -= CommandExecuting;
		}
		if (_interview != null)
		{
			_interview.StateChanged -= InterviewStateChanged;
		}
		_commands.Dispose();
		_menuStates.Clear();
	}
}
