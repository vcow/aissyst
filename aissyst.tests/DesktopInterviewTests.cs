using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using aissyst.core;
using aissyst.core.Audio;
using aissyst.core.Controller;
using aissyst.core.Database;
using aissyst.core.LLMConnector;
using aissyst.core.LLMConnector.DTO;
using aissyst.core.LLMConnector.Model;
using aissyst.core.Transcription;
using aissyst.desktop;
using aissyst.desktop.Dialog;
using aissyst.desktop.Interview;
using aissyst.desktop.Selectors;
using aissyst.desktop.Settings;
using aissyst.desktop.State;
using aissyst.desktop.ViewModels;
using aissyst.desktop.Views;
using aissyst.desktop.Wizard;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace aissyst.tests;

[Collection("Desktop UI")]
public sealed class DesktopInterviewTests : IDisposable
{
	private readonly HeadlessUnitTestSession _ui = HeadlessUnitTestSession.StartNew(typeof(App));

	[Fact]
	public Task ReadyPageWaitsForExplicitStartAndForceDialogTranscribesBothSourcesWithoutLlmRequest() => OnUI(async () =>
	{
		await using var fixture = new Fixture();
		await fixture.InitializeAsync();
		await fixture.ReadyAsync();
		var wizard = fixture.Wizard.Window!;
		var view = Assert.IsType<WizardViewModel>(wizard.DataContext);
		Assert.True(view.IsAction);
		Assert.False(view.IsWorking);
		Assert.Empty(view.Options);
		Assert.False(wizard.FindControl<ScrollViewer>("ChoicesScroll")!.IsVisible);
		Assert.False(wizard.FindControl<Button>("SecondaryButton")!.IsVisible);
		Assert.Equal("Start", wizard.FindControl<Button>("NextButton")!.Content);
		Assert.False(fixture.Starting!.IsCompleted);
		Assert.Equal(0, fixture.Audio.MicrophoneCaptures);
		Assert.Equal(0, fixture.Audio.ApplicationCaptures);
		Assert.False(fixture.Interview.IsStarted);
		Assert.False(Find(fixture.Main, "Interview", "Force Dialog").IsEnabled);
		Click(wizard.FindControl<Button>("NextButton")!);
		Assert.False(view.CanNext);
		Click(wizard.FindControl<Button>("NextButton")!);
		await fixture.Starting;
		await fixture.Wizard.CompleteAsync();
		Assert.True(fixture.Interview.IsStarted);
		Assert.Equal(1, fixture.Audio.MicrophoneCaptures);
		Assert.Equal(1, fixture.Audio.ApplicationCaptures);
		fixture.Transcriber.Recognize = (frames, _) => Task.FromResult(frames[0].Samples.Span[0] == 0.5f ? "Question" : "My answer");
		await fixture.Audio.EmitApplicationAsync(Frame(0.5f));
		await fixture.Audio.EmitMicrophoneAsync(Frame(0.25f));
		Click(Find(fixture.Main, "Interview", "Force Dialog"));
		await fixture.Shell.WaitForCommandsAsync();
		var dialog = fixture.Provider.GetRequiredService<DialogViewModel>().GetSnapshot();
		Assert.Contains(dialog, message => message.Text == "Question" && message.Source == AudioSourceType.Application);
		Assert.Contains(dialog, message => message.Text == "My answer" && message.Source == AudioSourceType.Microphone);
		Assert.Equal(0, fixture.Connector.Requests);
		Assert.True(fixture.Interview.IsStarted);
	});

	[Fact]
	public Task MenusHaveTheRequestedOrderAndWindowCheckboxesFollowShowingAndClosing() => OnUI(async () =>
	{
		await using var fixture = new Fixture();
		await fixture.InitializeAsync();
		await fixture.ReadyAsync();
		var appMenu = NativeMenu.GetMenu(Application.Current!)!;
		Assert.Equal(new[] { "Settings…", "|", "Quit" }, Captions(appMenu));
		Assert.Equal("Aisyst", Application.Current!.Name);
		Assert.Equal(new[] { "Force Dialog", "Translate", "Suggest", "Answer", "Translate & Answer", "Translate & Suggest",
			"|", "Reconnect…", "Save Dialog…" }, Captions(Find(fixture.Main, "Interview").Menu!));
		Assert.Equal(new[] { "Log", "Dialog", "Prompt", "|", "Fit to Screen" }, Captions(Find(fixture.Main, "Windows").Menu!));
		foreach (var caption in new[] { "Translate", "Suggest", "Answer", "Translate & Answer", "Translate & Suggest" })
		{
			Assert.True(Find(fixture.Main, "Interview", caption).IsEnabled);
		}
		Assert.True(Find(fixture.Main, "Windows", "Prompt").IsEnabled);
		Assert.Equal(MenuItemToggleType.CheckBox, Find(fixture.Main, "Windows", "Prompt").ToggleType);
		Assert.True(Find(fixture.Main, "Windows", "Log").IsChecked);
		Assert.True(Find(fixture.Main, "Windows", "Dialog").IsChecked);
		Click(Find(fixture.Main, "Windows", "Log"));
		Assert.False(fixture.Main.IsVisible);
		Assert.False(Find(fixture.Wizard.Window!, "Windows", "Log").IsChecked);
		fixture.Dialog.Window!.Close();
		Assert.False(Find(fixture.Wizard.Window!, "Windows", "Dialog").IsChecked);
		Click(Find(fixture.Wizard.Window!, "Windows", "Dialog"));
		Assert.True(fixture.Dialog.Window.IsVisible);
		Assert.False(fixture.Main.IsVisible);
		Click(Find(fixture.Dialog.Window, "Windows", "Log"));
		Assert.True(fixture.Main.IsVisible);
		Assert.True(Find(fixture.Wizard.Window!, "Windows", "Log").IsChecked);
	});

	[Fact]
	public Task ReconnectNeedsYesAndRestoresTheSessionWithoutStartingAudio() => OnUI(async () =>
	{
		await using var fixture = new Fixture();
		await fixture.InitializeAsync();
		await fixture.ReadyAsync();
		Click(Find(fixture.Main, "Interview", "Reconnect…"));
		var confirmation = Assert.IsType<ConfirmationWindow>(fixture.Shell.Confirmation);
		Assert.True(confirmation.Topmost);
		Assert.True(confirmation.IsVisible);
		Assert.False(fixture.Main.IsEnabled);
		Assert.False(fixture.Dialog.Window!.IsEnabled);
		Assert.False(fixture.Wizard.Window!.IsEnabled);
		Assert.Contains(confirmation.GetLogicalDescendants().OfType<TextBlock>(), block => block.Text == "Are you sure?");
		Click(confirmation.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "NoButton"));
		await fixture.Shell.WaitForCommandsAsync();
		Assert.Equal(0, fixture.Connector.Reconnects);
		Assert.True(fixture.Main.IsEnabled);
		Click(Find(fixture.Main, "Interview", "Reconnect…"));
		confirmation = fixture.Shell.Confirmation!;
		Click(confirmation.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "YesButton"));
		await fixture.Shell.WaitForCommandsAsync();
		Assert.Equal(1, fixture.Connector.Reconnects);
		Assert.Equal(2, fixture.Connector.SessionInitializations);
		Assert.True(fixture.Provider.GetRequiredService<SessionModel>().IsReady);
		Assert.Equal(0, fixture.Audio.MicrophoneCaptures);
		Assert.True(fixture.Wizard.Window.IsVisible);
	});

	[Fact]
	public Task ShutdownClosesPendingConfirmationWithoutReconnecting() => OnUI(async () =>
	{
		await using var fixture = new Fixture();
		await fixture.InitializeAsync();
		await fixture.ReadyAsync();
		Click(Find(fixture.Main, "Interview", "Reconnect…"));
		Assert.NotNull(fixture.Shell.Confirmation);
		fixture.Shell.PrepareShutdown();
		await fixture.Shell.WaitForCommandsAsync().WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Null(fixture.Shell.Confirmation);
		Assert.Equal(0, fixture.Connector.Reconnects);
	});

	[Fact]
	public Task StartFailureKeepsTheWizardOpenAndAllowsAnotherAttempt() => OnUI(async () =>
	{
		await using var fixture = new Fixture(invalidCaptureSettings: true);
		await fixture.InitializeAsync();
		await fixture.ReadyAsync();
		var wizard = fixture.Wizard.Window!;
		var view = (WizardViewModel)wizard.DataContext!;
		for (var attempt = 0; attempt < 2; attempt++)
		{
			Click(wizard.FindControl<Button>("NextButton")!);
			await Until(() => view.HasError && view.CanNext);
			Assert.True(wizard.IsVisible);
			Assert.Equal("Start", view.NextLabel);
			Assert.False(fixture.Interview.IsStarted);
			Assert.Equal(0, fixture.Audio.MicrophoneCaptures);
		}
	});

	[Fact]
	public Task ForceDialogWaitsForTranscriptionAndRejectsRepeatedClicksWithoutDisablingCommands() => OnUI(async () =>
	{
		await using var fixture = new Fixture();
		await fixture.InitializeAsync();
		await fixture.ReadyAsync();
		Click(fixture.Wizard.Window!.FindControl<Button>("NextButton")!);
		await fixture.Starting!;
		await fixture.Wizard.CompleteAsync();
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		fixture.Transcriber.Recognize = async (_, token) =>
		{
			entered.TrySetResult();
			await release.Task.WaitAsync(token);
			return "Question before Force Dialog";
		};
		try
		{
			await fixture.Audio.EmitApplicationAsync(Frame(0.5f));
			Click(Find(fixture.Main, "Interview", "Force Dialog"));
			await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
			Assert.True(Find(fixture.Main, "Interview", "Force Dialog").IsEnabled);
			Click(Find(fixture.Main, "Interview", "Force Dialog"));
			Assert.Single(fixture.Transcriber.Requests);
			release.TrySetResult();
			await fixture.Shell.WaitForCommandsAsync();
			Assert.Equal("Question before Force Dialog", Assert.Single(fixture.Provider.GetRequiredService<DialogViewModel>().GetSnapshot()).Text);
			Assert.Equal(0, fixture.Connector.Requests);
		}
		finally
		{
			release.TrySetResult();
		}
	});

	internal sealed class Fixture : IAsyncDisposable
	{
		internal readonly MainWindow Main = new() { DataContext = new LogViewModel() };
		internal readonly TestAudioCapture Audio = new();
		internal readonly TestTranscriber Transcriber = new();
		internal readonly TestLLMConnector Connector = new() { Initialize = (session, _) => Task.FromResult(new DialogResponse
		{
			Summary = session.Dialog.Count == 0 ? "" : "Restored", SummaryTimestamp = session.Dialog.LastOrDefault()?.Timestamp
		}) };
		internal readonly WizardService Wizard;
		internal readonly ServiceProvider Provider;
		internal readonly DesktopInterview Interview;
		internal readonly DesktopDialogService Dialog;
		internal readonly DesktopApplicationShell Shell;
		internal readonly InterviewCommands Commands;
		internal readonly DesktopPromptService Prompt;
		internal readonly DesktopSettingsStore Settings;
		internal readonly DesktopSettingsDialog SettingsDialog;
		internal Task? Starting;
		private readonly CancellationTokenSource _cancel = new();
		internal Fixture(bool invalidCaptureSettings = false)
		{
			var state = new DesktopWindowState();
			Wizard = new WizardService(NullLogger<WizardService>.Instance, state);
			var settings = new DesktopSettingsStore(Path.Combine(Path.GetTempPath(), $"aissyst-test-{Guid.NewGuid():N}.json"));
			Settings = settings;
			var services = DesktopServices.Create(Main, (LogViewModel)Main.DataContext!, settings, state);
			services.AddSingleton<IDatabase>(new TestSessionDatabase());
			services.AddSingleton<ISessionSelector>(new TestSessionSelector());
			services.AddSingleton<IAudioCapture>(Audio);
			services.AddSingleton<ITranscriber>(Transcriber);
			services.AddSingleton<ILLMConnector>(Connector);
			services.AddSingleton<IWizardService>(_ => new SetupWizard(Wizard));
			services.AddSingleton<IProgress<InitializationProgress>>(new Progress<InitializationProgress>(_ => { }));
			if (invalidCaptureSettings)
			{
				services.AddSingleton(provider => new DesktopInterview(provider.GetRequiredService<ApplicationController>(),
					provider.GetRequiredService<SessionModel>(), provider.GetRequiredService<DesktopCaptureSession>(), Audio,
					new DesktopApplicationSettings { AvailableSources = AudioSourceType.None }, provider.GetRequiredService<IWizardService>(),
					NullLogger<DesktopInterview>.Instance));
			}
			Provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
			Interview = Provider.GetRequiredService<DesktopInterview>();
			Dialog = Provider.GetRequiredService<DesktopDialogService>();
			Commands = Provider.GetRequiredService<InterviewCommands>();
			Prompt = Provider.GetRequiredService<DesktopPromptService>();
			var settingsDialog = new DesktopSettingsDialog(settings, () => Main, () => new Window[] { Main, Dialog.Window!, Prompt.Window! },
				NullLogger<SettingsWindow>.Instance, state);
			SettingsDialog = settingsDialog;
			Shell = new DesktopApplicationShell(Application.Current!, Main, settingsDialog, () => { },
				NullLogger<DesktopApplicationShell>.Instance, Dialog, state, Interview, Commands, Prompt);
			Shell.OpenStartupWindows();
		}
		internal Task InitializeAsync() => Provider.GetRequiredService<ApplicationController>().InitializeAsync(_cancel.Token);
		internal async Task ReadyAsync()
		{
			Starting = Interview.WaitForStartAsync(_cancel.Token);
			await Until(() => Wizard.Window?.DataContext is WizardViewModel { IsAction: true, IsWaiting: false });
		}
		public async ValueTask DisposeAsync()
		{
			Shell.PrepareShutdown();
			await _cancel.CancelAsync();
			if (Starting != null)
			{
				try { await Starting; }
				catch (OperationCanceledException) { }
			}
			await Shell.WaitForCommandsAsync();
			await Provider.DisposeAsync();
			Shell.AllowClose();
			Main.Close();
			_cancel.Dispose();
			if (File.Exists(Settings.FilePath)) { File.Delete(Settings.FilePath); }
		}
	}
	private sealed class SetupWizard(WizardService ready) : IWizardService, IAsyncDisposable
	{
		public Task<string> ShowAsync(WizardPage page, CancellationToken cancellationToken = default) => page.IsAction
			? ready.ShowAsync(page, cancellationToken)
			: Task.FromResult(page.Options.First(option => option.Key != "none").Key);
		public ValueTask DisposeAsync() => ready.DisposeAsync();
	}
	private static AudioFrame Frame(float sample) => new(Enumerable.Repeat(sample, 80).ToArray(), new AudioFormat(16000, 1), TimeSpan.Zero);
	private static NativeMenuItem Find(Window window, params string[] path)
	{
		var menu = NativeMenu.GetMenu(window)!;
		NativeMenuItem? item = null;
		foreach (var caption in path)
		{
			item = menu.Items.OfType<NativeMenuItem>().Single(candidate => candidate.Header == caption);
			menu = item.Menu!;
		}
		return item!;
	}
	private static string[] Captions(NativeMenu menu) => menu.Items.Select(item =>
		item is NativeMenuItemSeparator ? "|" : ((NativeMenuItem)item).Header!).ToArray();
	private static void Click(NativeMenuItem item) => ((INativeMenuItemExporterEventsImplBridge)item).RaiseClicked();
	private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
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
	public void Dispose() => _ui.Dispose();
}
