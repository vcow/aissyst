using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media;
using aissyst.desktop;
using aissyst.desktop.Dialog;
using aissyst.desktop.Settings;
using aissyst.desktop.State;
using aissyst.desktop.ViewModels;
using aissyst.desktop.Views;
using aissyst.desktop.Wizard;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace aissyst.tests;

[Collection("Desktop UI")]
public sealed class DesktopWindowStateTests : IDisposable
{
	private readonly HeadlessUnitTestSession _ui = HeadlessUnitTestSession.StartNew(typeof(App));
	private readonly string _directory = Path.Combine(Path.GetTempPath(), $"aissyst-window-state-{Guid.NewGuid():N}");
	private string StatePath => Path.Combine(_directory, "desktop-ui-state.json");

	[Fact]
	public Task WorkingWindowsOpenBeforeSetupAndRestoreTheirPositionsAndVisibilityAfterRestart() => OnUI(async () =>
	{
		var state = new DesktopWindowState(StatePath);
		var main = new MainWindow { DataContext = new LogViewModel() };
		var dialog = new DesktopDialogService(new DialogViewModel(), NullLogger<DesktopDialogService>.Instance, windowState: state);
		var settings = Settings(main, state);
		var shell = Shell(main, settings, dialog, state);
		shell.OpenStartupWindows();
		Assert.True(main.IsVisible);
		Assert.True(dialog.Window!.IsVisible);
		Assert.Null(settings.Window);
		var dialogColor = Assert.IsAssignableFrom<ISolidColorBrush>(dialog.Window.Background).Color;
		Assert.Equal(Color.Parse("#101E3A"), dialogColor);
		Assert.Equal(dialogColor, Assert.IsAssignableFrom<ISolidColorBrush>(dialog.Window.FindControl<ListBox>("DialogList")!.Background).Color);
		Assert.NotEqual(dialogColor, Assert.IsAssignableFrom<ISolidColorBrush>(main.FindControl<ListBox>("LogList")!.Background).Color);

		await using (var wizard = new WizardService(NullLogger<WizardService>.Instance, state))
		{
			var request = wizard.ShowAsync(new("Setup", "Choose an item.", [new("one", "One")]) { SelectedKey = "one" });
			await Until(() => wizard.Window?.IsVisible == true);
			Assert.False(request.IsCompleted);
			Assert.True(main.IsVisible);
			Assert.True(dialog.Window.IsVisible);
			main.Position = new PixelPoint(111, 222);
			dialog.Window.Position = new PixelPoint(-1920, 90);
			wizard.Window!.Position = new PixelPoint(333, 444);
			ClickNext(wizard.Window);
			await request;
			await wizard.CompleteAsync();
		}
		main.Hide();
		shell.PrepareShutdown();
		await dialog.DisposeAsync();
		shell.AllowClose();
		main.Close();
		await state.DisposeAsync();

		await using var restarted = new DesktopWindowState(StatePath);
		AssertPlacement(new SavedWindowState(111, 222, false), restarted.Get("log"));
		AssertPlacement(new SavedWindowState(-1920, 90, true), restarted.Get("dialog"));
		Assert.Null(restarted.Get("wizard")!.IsVisible);
		var newMain = new MainWindow { DataContext = new LogViewModel() };
		await using var newDialog = new DesktopDialogService(new DialogViewModel(), NullLogger<DesktopDialogService>.Instance,
			windowState: restarted);
		var newShell = Shell(newMain, Settings(newMain, restarted), newDialog, restarted);
		try
		{
			var logOpened = false;
			newMain.Opened += (_, _) => logOpened = true;
			newShell.OpenStartupWindows();
			Assert.False(newMain.IsVisible);
			Assert.False(logOpened);
			Assert.Equal(new PixelPoint(111, 222), newMain.Position);
			Assert.True(newDialog.Window!.IsVisible);
			Assert.Equal(new PixelPoint(-1920, 90), newDialog.Window.Position);
			newShell.ShowLog();
			Assert.True(newMain.IsVisible);
			Assert.Equal(new PixelPoint(111, 222), newMain.Position);
			await using var wizard = new WizardService(NullLogger<WizardService>.Instance, restarted);
			var request = wizard.ShowAsync(new("Setup again", "Choose.", [new("one", "One")]) { SelectedKey = "one" });
			await Until(() => wizard.Window?.IsVisible == true);
			Assert.Equal(new PixelPoint(333, 444), wizard.Window!.Position);
			Assert.Equal(new PixelPoint(111, 222), newMain.Position);
			ClickNext(wizard.Window);
			await request;
			await wizard.CompleteAsync();
		}
		finally
		{
			newShell.PrepareShutdown();
			newShell.AllowClose();
			newMain.Close();
		}
	});

	[Fact]
	public Task BothWorkingWindowsCanStayHiddenWhileTheMandatoryWizardStillOpens() => OnUI(async () =>
	{
		Directory.CreateDirectory(_directory);
		await File.WriteAllTextAsync(StatePath, JsonSerializer.Serialize(new Dictionary<string, SavedWindowState>
		{
			["log"] = new(-20_000, 30_000, false), ["dialog"] = new(40_000, -50_000, false),
			["wizard"] = new(123, 234, false)
		}));
		await using var state = new DesktopWindowState(StatePath);
		var main = new MainWindow { DataContext = new LogViewModel() };
		await using var dialog = new DesktopDialogService(new DialogViewModel(), NullLogger<DesktopDialogService>.Instance, windowState: state);
		var shell = Shell(main, Settings(main, state), dialog, state);
		try
		{
			shell.OpenStartupWindows();
			Assert.False(main.IsVisible);
			Assert.False(dialog.Window!.IsVisible);
			Assert.Equal(new PixelPoint(-20_000, 30_000), main.Position);
			Assert.Equal(new PixelPoint(40_000, -50_000), dialog.Window.Position);
			await using var wizard = new WizardService(NullLogger<WizardService>.Instance, state);
			var request = wizard.ShowAsync(new("Setup", "Choose.", [new("one", "One")]) { SelectedKey = "one" });
			await Until(() => wizard.Window?.IsVisible == true);
			Assert.Equal(new PixelPoint(123, 234), wizard.Window!.Position);
			ClickNext(wizard.Window);
			await request;
			await wizard.CompleteAsync();
			Assert.False(main.IsVisible);
			Assert.False(dialog.Window.IsVisible);
			shell.ShowDialog();
			Assert.True(dialog.Window.IsVisible);
			Assert.Equal(new PixelPoint(40_000, -50_000), dialog.Window.Position);
		}
		finally
		{
			shell.PrepareShutdown();
			shell.AllowClose();
			main.Close();
		}
	});

	[Fact]
	public Task SettingsPositionIsServiceStateAndRestoreDefaultsDoesNotChangeIt() => OnUI(async () =>
	{
		await using var state = new DesktopWindowState(StatePath);
		var main = new Window();
		main.Show();
		var functional = new DesktopSettingsStore(Path.Combine(_directory, "desktop-settings.json"));
		var settings = new DesktopSettingsDialog(functional, () => main, () => [main], NullLogger<SettingsWindow>.Instance, state);
		try
		{
			var shown = settings.ShowAsync();
			settings.Window!.Position = new PixelPoint(456, 567);
			settings.Close();
			await shown;
			await state.FlushAsync();
			Assert.Null(state.Get("settings")!.IsVisible);
			Assert.False(File.Exists(functional.FilePath));
			shown = settings.ShowAsync();
			Assert.Equal(new PixelPoint(456, 567), settings.Window!.Position);
			var model = Assert.IsType<SettingsViewModel>(settings.Window.DataContext);
			model.RestoreDefaults();
			await functional.SaveAsync(model.GetSettings());
			Assert.DoesNotContain("456", await File.ReadAllTextAsync(functional.FilePath));
			Assert.Equal(new PixelPoint(456, 567), settings.Window.Position);
			settings.Close();
			await shown;
			await state.FlushAsync();
			var ui = await File.ReadAllTextAsync(StatePath);
			Assert.DoesNotContain(nameof(DesktopApplicationSettings.TranscriptionModelPath), ui);
			Assert.DoesNotContain("MaxTextProcessingCount", ui);
		}
		finally
		{
			settings.Close();
			await settings.WaitForCloseAsync();
			main.Close();
		}
	});

	[Fact]
	public Task MovementIsDebouncedAndShutdownDoesNotSaveTheVisibilityOfClosingWindows() => OnUI(async () =>
	{
		var state = new DesktopWindowState(StatePath);
		var window = new Window();
		state.Attach(window, "log");
		window.Show();
		for (var index = 0; index < 100; index++)
		{
			window.Position = new PixelPoint(index, index + 1);
		}
		Assert.False(File.Exists(StatePath));
		await Until(() => File.Exists(StatePath));
		var stored = JsonSerializer.Deserialize<Dictionary<string, SavedWindowState>>(await File.ReadAllTextAsync(StatePath))!;
		AssertPlacement(new SavedWindowState(99, 100, true), stored["log"]);
		state.StopTracking();
		window.Hide();
		window.Close();
		await state.DisposeAsync();
		await using var restarted = new DesktopWindowState(StatePath);
		Assert.True(restarted.ShouldShow("log"));
		AssertPlacement(new SavedWindowState(99, 100, true), restarted.Get("log"));
		Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
	});

	[Theory]
	[InlineData("not JSON")]
	[InlineData("{\"log\":null}")]
	public Task InvalidServiceStateUsesDefaultsWithoutBlockingStartup(string content) => OnUI(async () =>
	{
		Directory.CreateDirectory(_directory);
		await File.WriteAllTextAsync(StatePath, content);
		await using var state = new DesktopWindowState(StatePath);
		Assert.NotNull(state.LoadError);
		Assert.True(state.ShouldShow("log"));
		Assert.True(state.ShouldShow("dialog"));
		Assert.Null(state.Get("log"));
		Assert.Equal(content, await File.ReadAllTextAsync(StatePath));
	});

	[Fact]
	public Task ShowingHiddenLogWithDialogOpenAndImmediatelyQuittingSavesVisibilityPositionAndSize() => OnUI(async () =>
	{
		Directory.CreateDirectory(_directory);
		await File.WriteAllTextAsync(StatePath,
			"{\"log\":{\"X\":234,\"Y\":558,\"IsVisible\":false},\"dialog\":{\"X\":1006,\"Y\":109,\"IsVisible\":true}}");
		var state = new DesktopWindowState(StatePath);
		var main = new MainWindow { DataContext = new LogViewModel() };
		var dialog = new DesktopDialogService(new DialogViewModel(), NullLogger<DesktopDialogService>.Instance, windowState: state);
		var shell = Shell(main, Settings(main, state), dialog, state);
		shell.OpenStartupWindows();
		Assert.False(main.IsVisible);
		Assert.True(dialog.Window!.IsVisible);
		var menu = NativeMenu.GetMenu(main)!.Items.OfType<NativeMenuItem>().Single(item => item.Header == "Windows").Menu!;
		((INativeMenuItemExporterEventsImplBridge)menu.Items[0]).RaiseClicked();
		Assert.True(main.IsVisible);
		main.Position = new PixelPoint(321, 432);
		main.Width = 900;
		main.Height = 380;
		main.UpdateLayout();
		await Until(() => main.ClientSize == new Size(900, 380));
		shell.PrepareShutdown();
		await dialog.DisposeAsync();
		shell.AllowClose();
		main.Close();
		await state.DisposeAsync();

		await using var restarted = new DesktopWindowState(StatePath);
		Assert.Equal(new SavedWindowState(321, 432, true, 900, 380), restarted.Get("log"));
		var newMain = new MainWindow { DataContext = new LogViewModel() };
		await using var newDialog = new DesktopDialogService(new DialogViewModel(), NullLogger<DesktopDialogService>.Instance,
			windowState: restarted);
		var newShell = Shell(newMain, Settings(newMain, restarted), newDialog, restarted);
		try
		{
			newShell.OpenStartupWindows();
			Assert.True(newMain.IsVisible);
			Assert.True(newDialog.Window!.IsVisible);
			Assert.Equal(new PixelPoint(321, 432), newMain.Position);
			Assert.Equal(new Size(900, 380), newMain.ClientSize);
		}
		finally
		{
			newShell.PrepareShutdown();
			newShell.AllowClose();
			newMain.Close();
		}
	});

	[Fact]
	public Task ResizingAloneIsSavedAndMinimizedOrHiddenSizesDoNotReplaceNormalGeometry() => OnUI(async () =>
	{
		var state = new DesktopWindowState(StatePath);
		var window = new Window { Width = 600, Height = 400 };
		state.Attach(window, "dialog");
		window.Show();
		window.Position = new PixelPoint(123, 234);
		await state.FlushAsync();
		window.Width = 780;
		window.Height = 520;
		window.UpdateLayout();
		await Until(() => state.Get("dialog")?.Width == 780);
		await Until(() =>
		{
			var data = JsonSerializer.Deserialize<Dictionary<string, SavedWindowState>>(File.ReadAllText(StatePath))!;
			return data["dialog"].Width == 780 && data["dialog"].Height == 520;
		});
		window.WindowState = WindowState.Minimized;
		window.Width = 1000;
		window.Height = 700;
		window.UpdateLayout();
		Assert.Equal(new SavedWindowState(123, 234, true, 780, 520), state.Get("dialog"));
		window.Hide();
		window.Width = 1100;
		window.Height = 800;
		window.UpdateLayout();
		state.StopTracking();
		window.Close();
		await state.DisposeAsync();
		await using var restarted = new DesktopWindowState(StatePath);
		Assert.Equal(new SavedWindowState(123, 234, false, 780, 520), restarted.Get("dialog"));
	});

	[Theory]
	[InlineData(null, null)]
	[InlineData(0d, 450d)]
	[InlineData(800d, -1d)]
	public Task MissingOrInvalidSizesKeepWindowDefaultsAndPreservePositionAndVisibility(double? width, double? height) => OnUI(async () =>
	{
		Directory.CreateDirectory(_directory);
		await File.WriteAllTextAsync(StatePath, JsonSerializer.Serialize(new Dictionary<string, SavedWindowState>
		{
			["log"] = new(123, 234, false, width, height)
		}));
		await using var state = new DesktopWindowState(StatePath);
		var window = new Window { Width = 600, Height = 400 };
		state.Attach(window, "log");
		Assert.Null(state.LoadError);
		Assert.False(state.ShouldShow("log"));
		Assert.Equal(new PixelPoint(123, 234), window.Position);
		Assert.Equal(600, window.Width);
		Assert.Equal(400, window.Height);
		state.StopTracking();
		window.Close();
	});

	[Fact]
	public Task FixedSizeWindowsIgnoreSavedDimensions() => OnUI(async () =>
	{
		Directory.CreateDirectory(_directory);
		await File.WriteAllTextAsync(StatePath, JsonSerializer.Serialize(new Dictionary<string, SavedWindowState>
		{
			["wizard"] = new(123, 234, null, 1200, 900)
		}));
		await using var state = new DesktopWindowState(StatePath);
		var window = new WizardWindow();
		state.Attach(window, "wizard", rememberVisibility: false);
		window.Show();
		Assert.Equal(new Size(620, 520), window.ClientSize);
		Assert.Equal(new PixelPoint(123, 234), window.Position);
		Assert.Equal(new SavedWindowState(123, 234, null), state.Get("wizard"));
		state.StopTracking();
		window.Complete();
	});

	[Fact]
	public Task ExplicitFitMovesAndSavesHiddenWindowsWithoutShowingThem() => OnUI(async () =>
	{
		Directory.CreateDirectory(_directory);
		await File.WriteAllTextAsync(StatePath, JsonSerializer.Serialize(new Dictionary<string, SavedWindowState>
		{
			["log"] = new(5000, -5000, false, 1600, 1200)
		}));
		var state = new DesktopWindowState(StatePath);
		var window = new MainWindow { DataContext = new LogViewModel() };
		state.Attach(window, "log");
		Assert.Equal(new PixelPoint(5000, -5000), window.Position);
		state.FitToScreens([new(new PixelRect(0, 25, 1000, 700), 1)]);
		Assert.False(window.IsVisible);
		Assert.Equal(new PixelPoint(0, 25), window.Position);
		Assert.InRange(window.Width, 320, 1000);
		Assert.InRange(window.Height, 200, 700);
		state.StopTracking();
		window.Close();
		await state.DisposeAsync();
		await using var restored = new DesktopWindowState(StatePath);
		Assert.Equal(new PixelPoint(0, 25), new PixelPoint(restored.Get("log")!.X, restored.Get("log")!.Y));
		Assert.False(restored.ShouldShow("log"));
		Assert.InRange(restored.Get("log")!.Width!.Value, 320, 1000);
	});

	[Fact]
	public Task ServiceProviderFlushesTheLatestMoveBeforeApplicationExit() => OnUI(async () =>
	{
		var state = new DesktopWindowState(StatePath);
		var main = new MainWindow { DataContext = new LogViewModel() };
		var services = DesktopServices.Create(main, new LogViewModel(),
			new DesktopSettingsStore(Path.Combine(_directory, "desktop-settings.json")), state);
		var provider = services.BuildServiceProvider();
		var managed = provider.GetRequiredService<DesktopWindowState>();
		Assert.Same(state, managed);
		managed.Attach(main, "log");
		main.Show();
		main.Position = new PixelPoint(765, 876);
		Assert.False(File.Exists(StatePath));
		managed.StopTracking();
		main.Close();
		await provider.DisposeAsync();
		await using var restarted = new DesktopWindowState(StatePath);
		AssertPlacement(new SavedWindowState(765, 876, true), restarted.Get("log"));
	});

	[Fact]
	public Task StateWriteFailureKeepsTheLiveStateAndDisposalStillCompletes() => OnUI(async () =>
	{
		Directory.CreateDirectory(StatePath);
		var state = new DesktopWindowState(StatePath);
		var errors = 0;
		state.SaveFailed += _ => errors++;
		var window = new Window();
		state.Attach(window, "log");
		window.Show();
		window.Position = new PixelPoint(400, 500);
		await Assert.ThrowsAnyAsync<IOException>(() => state.FlushAsync());
		AssertPlacement(new SavedWindowState(400, 500, true), state.Get("log"));
		state.StopTracking();
		window.Close();
		await state.DisposeAsync();
		Assert.True(errors > 0);
		Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
	});

	private DesktopSettingsDialog Settings(Window owner, DesktopWindowState state) => new(
		new DesktopSettingsStore(Path.Combine(_directory, "desktop-settings.json")), () => owner, () => [owner],
		NullLogger<SettingsWindow>.Instance, state);
	private static DesktopApplicationShell Shell(MainWindow main, DesktopSettingsDialog settings,
		DesktopDialogService dialog, DesktopWindowState state) => new(Application.Current!, main, settings, () => { },
		NullLogger<DesktopApplicationShell>.Instance, dialog, state);
	private static void AssertPlacement(SavedWindowState expected, SavedWindowState? actual)
	{
		Assert.NotNull(actual);
		Assert.Equal((expected.X, expected.Y, expected.IsVisible), (actual.X, actual.Y, actual.IsVisible));
	}
	private static void ClickNext(Window window) => window.FindControl<Button>("NextButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
	private async Task OnUI(Func<Task> action)
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
		await _ui.Dispatch(async () => { await action(); return true; }, timeout.Token);
	}
	private static async Task Until(Func<bool> condition)
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		while (!condition())
		{
			await Task.Delay(10, timeout.Token);
		}
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
