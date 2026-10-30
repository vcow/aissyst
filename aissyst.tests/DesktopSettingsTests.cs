using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using aissyst.core;
using aissyst.desktop;
using aissyst.desktop.Settings;
using aissyst.desktop.ViewModels;
using aissyst.desktop.Views;
using Microsoft.Extensions.Logging.Abstractions;

namespace aissyst.tests;

[Collection("Desktop UI")]
public sealed class DesktopSettingsTests : IDisposable
{
	private readonly HeadlessUnitTestSession _ui = HeadlessUnitTestSession.StartNew(typeof(MenuStartupProbe));
	private readonly string _directory = Path.Combine(Path.GetTempPath(), $"aissyst-settings-ui-{Guid.NewGuid():N}");

	[Fact]
	public Task ModalSettingsValidateSaveCancelAndRestoreAllFields() => OnUI(async () =>
	{
		var store = new DesktopSettingsStore(Path.Combine(_directory, "settings.json"));
		var owner = new Window();
		owner.Show();
		var dialogs = new DesktopSettingsDialog(store, () => owner, () => [owner], NullLogger<SettingsWindow>.Instance);
		try
		{
			var first = dialogs.ShowAsync();
			var window = dialogs.Window!;
			Assert.False(owner.IsEnabled);
			var model = Assert.IsType<SettingsViewModel>(window.DataContext);
			Assert.Equal(store.Active, model.GetSettings());
			Assert.Same(first, dialogs.ShowAsync());
			window.FindControl<TabControl>("SettingsTabs")!.SelectedIndex = 2;
			var textSlider = window.FindControl<Slider>("TextConcurrency")!;
			var transcriptionSlider = window.FindControl<Slider>("TranscriptionConcurrency")!;
			textSlider.Value = 999;
			transcriptionSlider.Value = -1;
			Assert.Equal(5, model.TextConcurrency);
			Assert.Equal(1, model.TranscriptionConcurrency);
			model.ModelUrl = "not a URL";
			Click(window, "SaveSettingsButton");
			Assert.True(window.IsVisible);
			Assert.True(model.HasError);
			Assert.False(File.Exists(store.FilePath));
			Click(window, "CancelSettingsButton");
			await first;
			Assert.True(owner.IsEnabled);
			Assert.Equal(new DesktopApplicationSettings(), store.Saved);

			var second = dialogs.ShowAsync();
			window = dialogs.Window!;
			model = (SettingsViewModel)window.DataContext!;
			window.FindControl<ComboBox>("AvailableSources")!.SelectedIndex = 2;
			Assert.Equal(AudioSourceType.Application, model.SelectedSource!.Value);
			model.SilenceSeconds = 2.5m;
			model.FragmentSeconds = 40;
			model.SignalThreshold = 0.05m;
			model.QueueCapacity = 20;
			model.TextConcurrency = 4;
			model.TranscriptionConcurrency = 2;
			model.ModelPath = Path.Combine(_directory, "custom.bin");
			model.ModelUrl = "https://example.invalid/model.bin";
			model.ModelSha256 = new string('b', 64);
			model.Language = "fr";
			model.UseMarkdown = false;
			var changed = model.GetSettings();
			Click(window, "SaveSettingsButton");
			await second;
			Assert.Equal(changed, new DesktopSettingsStore(store.FilePath).Active);
			Assert.NotEqual(changed, store.Active);

			var third = dialogs.ShowAsync();
			window = dialogs.Window!;
			model = (SettingsViewModel)window.DataContext!;
			Assert.Equal(changed, model.GetSettings());
			Click(window, "RestoreDefaultsButton");
			Assert.Equal(new DesktopApplicationSettings(), model.GetSettings());
			Assert.Equal(changed, store.Saved);
			Click(window, "CancelSettingsButton");
			await third;
			Assert.Equal(changed, new DesktopSettingsStore(store.FilePath).Active);

			var fourth = dialogs.ShowAsync();
			Click(dialogs.Window!, "RestoreDefaultsButton");
			Click(dialogs.Window!, "SaveSettingsButton");
			await fourth;
			Assert.Equal(new DesktopApplicationSettings(), new DesktopSettingsStore(store.FilePath).Active);
		}
		finally
		{
			dialogs.Close();
			await dialogs.WaitForCloseAsync();
			owner.Close();
		}
	});

	[Fact]
	public Task ApplicationMenuKeepsLogAliveAndSettingsDisableAllWindowsUntilClosed() => OnUI(async () =>
	{
		// Match the native backend: capture the menu before OnFrameworkInitializationCompleted.
		var exportedMenu = Assert.IsType<NativeMenu>(MenuStartupProbe.ExportedMenu);
		var nativeHolder = new NativeMenuItem("aissyst") { Menu = exportedMenu };
		var log = new LogViewModel();
		var main = new MainWindow { DataContext = log };
		var wizard = new Window();
		var laterWindow = new Window();
		main.Show();
		wizard.Show();
		var store = new DesktopSettingsStore(Path.Combine(_directory, "settings.json"));
		var dialogs = new DesktopSettingsDialog(store, () => main, () => [main, wizard], NullLogger<SettingsWindow>.Instance);
		var quitCalls = 0;
		var closed = false;
		main.Closed += (_, _) => closed = true;
		var shell = new DesktopApplicationShell(Application.Current!, main, dialogs, () => quitCalls++,
			NullLogger<DesktopApplicationShell>.Instance);
		try
		{
			var menu = NativeMenu.GetMenu(Application.Current!)!;
			Assert.Same(exportedMenu, menu);
			Assert.Same(nativeHolder, menu.Parent);
			Assert.DoesNotContain(menu.Items.OfType<NativeMenuItem>(), item => item.Header == "About Avalonia");
			Assert.Equal("Settings…", Assert.IsType<NativeMenuItem>(menu.Items[0]).Header);
			Assert.Equal("Show Log", Assert.IsType<NativeMenuItem>(menu.Items[1]).Header);
			Assert.Equal("Quit", Assert.IsType<NativeMenuItem>(menu.Items[3]).Header);
			Assert.NotNull(NativeDock.GetMenu(Application.Current!));
			main.Close();
			if (OperatingSystem.IsMacOS())
			{
				Assert.False(main.IsVisible);
			}
			else
			{
				Assert.Equal(WindowState.Minimized, main.WindowState);
			}
			Assert.False(closed);
			Assert.Equal(0, quitCalls);
			((INativeMenuItemExporterEventsImplBridge)exportedMenu.Items[1]).RaiseClicked();
			Assert.True(main.IsVisible);
			((INativeMenuItemExporterEventsImplBridge)exportedMenu.Items[0]).RaiseClicked();
			Assert.True(dialogs.Window!.IsVisible);
			Assert.False(main.IsEnabled);
			Assert.False(wizard.IsEnabled);
			laterWindow.Show();
			Assert.False(laterWindow.IsEnabled);
			var settingsWindow = dialogs.Window;
			Click(main, "SettingsMenuItem", MenuItem.ClickEvent);
			Assert.Same(settingsWindow, dialogs.Window);
			dialogs.Close();
			await dialogs.WaitForCloseAsync();
			Assert.True(main.IsEnabled);
			Assert.True(wizard.IsEnabled);
			Assert.True(laterWindow.IsEnabled);
			((INativeMenuItemExporterEventsImplBridge)exportedMenu.Items[3]).RaiseClicked();
			Assert.Equal(1, quitCalls);
			shell.PrepareShutdown();
			shell.AllowClose();
			main.Close();
			Assert.True(closed);
		}
		finally
		{
			dialogs.Close();
			await dialogs.WaitForCloseAsync();
			shell.AllowClose();
			main.Close();
			wizard.Close();
			laterWindow.Close();
		}
	});

	private sealed class MenuStartupProbe
	{
		public static NativeMenu? ExportedMenu { get; private set; }
		public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
			.AfterSetup(builder => ExportedMenu = NativeMenu.GetMenu(builder.Instance!));
	}

	private static void Click(Window window, string name) => Click(window, name, Button.ClickEvent);
	private static void Click(Window window, string name, RoutedEvent routedEvent) =>
		window.FindControl<Control>(name)!.RaiseEvent(new RoutedEventArgs(routedEvent));

	private async Task OnUI(Func<Task> action)
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
		await _ui.Dispatch(async () => { await action(); return true; }, timeout.Token);
	}

	public void Dispose()
	{
		_ui.Dispose();
		if (Directory.Exists(_directory))
		{
			Directory.Delete(_directory, recursive: true);
		}
	}
}
