using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using aissyst.core;
using aissyst.core.Audio;
using aissyst.core.Controller;
using aissyst.core.Database;
using aissyst.core.LLMConnector;
using aissyst.core.LLMConnector.DTO;
using aissyst.core.LLMConnector.Model;
using aissyst.core.Transcription;
using aissyst.desktop;
using aissyst.desktop.Selectors;
using aissyst.desktop.Settings;
using aissyst.desktop.ViewModels;
using aissyst.desktop.Views;
using aissyst.desktop.Wizard;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace aissyst.tests;

[Collection("Desktop UI")]
public sealed class DesktopWizardTests : IDisposable
{
	private readonly HeadlessUnitTestSession _ui = HeadlessUnitTestSession.StartNew(typeof(App));
	public void Dispose() => _ui.Dispose();

	[Fact]
	public Task WindowOpensLazilyAndStaysOpenBetweenRequestsWithNextDisabled() => OnUI(async () =>
	{
		var owner = new Window();
		owner.Show();
		await using var wizard = new WizardService(() => owner, NullLogger<WizardService>.Instance);
		Assert.Empty(owner.OwnedWindows);
		var first = wizard.ShowAsync(new WizardPage("First", "Choose an item.", [new("one", "One"), new("two", "Two")]));
		await Until(() => wizard.Window is { IsVisible: true });
		var window = Assert.IsType<WizardWindow>(wizard.Window);
		var model = Assert.IsType<WizardViewModel>(window.DataContext);
		var next = window.FindControl<Button>("NextButton")!;
		Assert.False(next.IsEnabled);
		model.Options[1].IsSelected = true;
		Assert.True(next.IsEnabled);
		next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		Assert.False(next.IsEnabled);
		next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		Assert.Equal("two", await first);
		Assert.True(window.IsVisible);
		Assert.True(model.IsWaiting);

		var second = wizard.ShowAsync(new WizardPage("Second", "Another choice.", [new("three", "Three")]) { SelectedKey = "three" });
		await Until(() => model.Title == "Second" && !model.IsWaiting);
		Assert.Same(window, wizard.Window);
		Assert.Empty(owner.OwnedWindows);
		Assert.True(next.IsEnabled);
		next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		Assert.Equal("three", await second);
		await wizard.CompleteAsync();
		Assert.False(window.IsVisible);
		Assert.False(wizard.CancellationToken.IsCancellationRequested);
		owner.Close();
	});

	[Fact]
	public Task SessionActionsAndConfirmationStayInTheSameWindow() => OnUI(async () =>
	{
		var owner = new Window();
		owner.Show();
		await using var wizard = new WizardService(() => owner, NullLogger<WizardService>.Instance);
		var page = new WizardPage("Select a session", "Select a saved session or create a new one.",
		[
			new("session", "Saved session")
			{
				NextLabel = "Next (Open)",
				SecondaryAction = new("delete:session", "Delete", "Delete this session and its dialog?")
				{
					ConfirmationLabel = "Confirm Delete"
				}
			},
			new("create", "Create New")
		]);
		var request = wizard.ShowAsync(page);
		await Until(() => wizard.Window is { IsVisible: true });
		var window = Assert.IsType<WizardWindow>(wizard.Window);
		var model = Assert.IsType<WizardViewModel>(window.DataContext);
		var next = window.FindControl<Button>("NextButton")!;
		var delete = window.FindControl<Button>("SecondaryButton")!;
		Assert.False(delete.IsVisible);
		Assert.False(next.IsEnabled);
		model.Options[0].IsSelected = true;
		Assert.True(delete.IsVisible);
		Assert.Equal("Next (Open)", next.Content);
		model.Options[1].IsSelected = true;
		Assert.False(delete.IsVisible);
		Assert.Equal("Next", next.Content);
		model.Options[0].IsSelected = true;
		delete.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		Assert.True(model.IsConfirming);
		Assert.False(model.CanEdit);
		Assert.Equal("Cancel", next.Content);
		Assert.False(request.IsCompleted);
		Assert.Same(window, wizard.Window);
		Assert.Empty(owner.OwnedWindows);
		next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		Assert.False(model.IsConfirming);
		Assert.True(model.CanEdit);
		Assert.False(request.IsCompleted);
		Assert.Equal("Next (Open)", next.Content);
		delete.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		delete.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		Assert.False(next.IsEnabled);
		Assert.False(delete.IsEnabled);
		Assert.Equal("delete:session", await request);
		var refreshed = wizard.ShowAsync(page);
		await Until(() => !model.IsWaiting);
		Assert.False(model.IsConfirming);
		model.Options[0].IsSelected = true;
		next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		Assert.Equal("session", await refreshed);
		await wizard.CompleteAsync();
		owner.Close();
	});

	[Fact]
	public Task LongRadioListScrollsInsideTheWindow() => OnUI(async () =>
	{
		var owner = new Window();
		owner.Show();
		await using var wizard = new WizardService(() => owner, NullLogger<WizardService>.Instance);
		var request = wizard.ShowAsync(new WizardPage("Applications", "Choose an application.",
			Enumerable.Range(0, 300).Select(index => new WizardOption(index.ToString(), $"Application {index}")).ToArray()));
		await Until(() => wizard.Window is { IsVisible: true });
		var window = Assert.IsType<WizardWindow>(wizard.Window);
		window.UpdateLayout();
		var scroll = window.FindControl<ScrollViewer>("ChoicesScroll")!;
		Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
		scroll.ScrollToEnd();
		Assert.True(scroll.Offset.Y > 0);
		Assert.InRange(window.Bounds.Height, 360, 520);
		await wizard.CompleteAsync();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
		Assert.False(wizard.CancellationToken.IsCancellationRequested);
		owner.Close();
	});

	[Fact]
	public Task CancellationDuringWaitingStillAllowsProgrammaticShutdown() => OnUI(async () =>
	{
		var owner = new Window();
		owner.Show();
		await using var wizard = new WizardService(() => owner, NullLogger<WizardService>.Instance);
		using var cancellation = new CancellationTokenSource();
		var request = Task.Run(() => wizard.ShowAsync(new WizardPage("Choice", "Choose.", [new("one", "One")]), cancellation.Token));
		await Until(() => wizard.Window is { IsVisible: true });
		await cancellation.CancelAsync();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
		var window = Assert.IsType<WizardWindow>(wizard.Window);
		window.Close();
		Assert.True(window.IsVisible);
		await wizard.CompleteAsync();
		await Assert.ThrowsAsync<ObjectDisposedException>(() => wizard.ShowAsync(new WizardPage("Later", "Choose.", [])));
		Assert.Empty(owner.OwnedWindows);
		owner.Close();
	});

	[Fact]
	public Task WizardAndLogRemainIndependentAndCloseIsBlockedUntilCompletion() => OnUI(async () =>
	{
		var log = new Window { Width = 800, Height = 450, MinWidth = 320, MinHeight = 200 };
		log.Show();
		await using var wizard = new WizardService(() => log, NullLogger<WizardService>.Instance);
		var request = wizard.ShowAsync(new WizardPage("Setup", "Choose an option.", [new("one", "One")]));
		await Until(() => wizard.Window is { IsVisible: true });
		var window = wizard.Window!;
		Assert.True(log.IsVisible);
		Assert.True(log.IsEnabled);
		Assert.Null(window.Owner);
		Assert.Empty(log.OwnedWindows);
		Assert.Equal(WindowDecorations.BorderOnly, window.WindowDecorations);
		Assert.False(window.CanResize);
		Assert.Equal(620, window.Width);
		Assert.Equal(620, window.MinWidth);
		Assert.Equal(620, window.MaxWidth);
		Assert.Equal(520, window.Height);
		Assert.Equal(520, window.MinHeight);
		Assert.Equal(520, window.MaxHeight);
		var titleBar = window.FindControl<Border>("WizardTitleBar")!;
		Assert.True(titleBar.IsEffectivelyVisible);
		Assert.True(titleBar.IsHitTestVisible);
		Assert.Equal("aissyst - Setup", window.FindControl<TextBlock>("WizardTitle")!.Text);
		Assert.False(window.CanMinimize);
		var screen = log.Screens.ScreenFromWindow(log);
		if (screen != null)
		{
			Assert.True(log.Position.X + log.Width * log.RenderScaling <= window.Position.X);
		}
		log.Activate();
		window.Close();
		Assert.True(window.IsVisible);
		Assert.False(request.IsCompleted);
		Assert.False(wizard.CancellationToken.IsCancellationRequested);
		var model = Assert.IsType<WizardViewModel>(window.DataContext);
		model.Options[0].IsSelected = true;
		window.FindControl<Button>("NextButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		Assert.Equal("one", await request);
		window.Close();
		Assert.True(window.IsVisible);
		await wizard.FailAsync("Initialization failed.");
		Assert.True(model.IsFailed);
		Assert.True(titleBar.IsEffectivelyEnabled);
		Assert.Equal("aissyst - Setup", window.FindControl<TextBlock>("WizardTitle")!.Text);
		Assert.False(model.IsWorking);
		Assert.False(model.CanNext);
		Assert.True(window.IsVisible);
		await wizard.CompleteAsync();
		Assert.False(window.IsVisible);
		Assert.True(log.IsVisible);
		log.Close();
	});

	[Fact]
	public Task ProgressUpdatesUseTheBarAndCannotLeakIntoAnotherPage() => OnUI(async () =>
	{
		var log = new Window();
		log.Show();
		await using var wizard = new WizardService(() => log, NullLogger<WizardService>.Instance);
		var reporter = new DesktopStartupProgress(wizard);
		var first = wizard.ShowAsync(new WizardPage("First", "Choose.", [new("one", "One")]) { SelectedKey = "one" });
		await Until(() => wizard.Window is { IsVisible: true });
		var window = wizard.Window!;
		var model = Assert.IsType<WizardViewModel>(window.DataContext);
		var next = window.FindControl<Button>("NextButton")!;
		next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		await first;
		await Task.Run(() => reporter.Report(new InitializationProgress("Downloading transcription model", 154_000_000, 190_000_000)));
		await Until(() => !model.IsIndeterminate);
		var bar = window.FindControl<ProgressBar>("StartupProgressBar")!;
		Assert.False(bar.IsIndeterminate);
		Assert.InRange(bar.Value, 81, 82);
		Assert.Contains("81%", model.ProgressText);
		Assert.Contains("154.0 / 190.0 MB", model.ProgressText);
		Assert.False(next.IsEnabled);

		var changes = 0;
		model.PropertyChanged += (_, args) =>
		{
			if (args.PropertyName == nameof(WizardViewModel.ProgressValue))
			{
				changes++;
			}
		};
		for (var index = 1; index <= 10_000; index++)
		{
			reporter.Report(new InitializationProgress("Downloading transcription model", index * 19_000L, 190_000_000));
		}
		Dispatcher.UIThread.RunJobs();
		Assert.Equal(100, bar.Value);
		Assert.Equal(1, changes);
		reporter.Report(new InitializationProgress("Downloading transcription model", 50_000_000));
		Dispatcher.UIThread.RunJobs();
		Assert.True(bar.IsIndeterminate);
		Assert.Contains("50.0 MB", model.ProgressText);
		Assert.DoesNotContain("%", model.ProgressText);
		reporter.Report(new InitializationProgress("Verifying transcription model SHA-256..."));
		Dispatcher.UIThread.RunJobs();
		Assert.True(bar.IsIndeterminate);
		Assert.Contains("SHA-256", model.ProgressText);

		reporter.Report(new InitializationProgress("Old queued progress", 1, 2));
		var second = wizard.ShowAsync(new WizardPage("Second", "Choose.", [new("two", "Two")]) { SelectedKey = "two" });
		await Until(() => model.Title == "Second" && !model.IsWaiting);
		next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		await second;
		Dispatcher.UIThread.RunJobs();
		Assert.Equal("Please wait...", model.ProgressText);
		Assert.True(model.IsIndeterminate);
		await wizard.FailAsync("Test failure.");
		reporter.Report(new InitializationProgress("Late progress", 2, 2));
		Dispatcher.UIThread.RunJobs();
		Assert.True(model.IsFailed);
		Assert.False(model.IsWorking);
		Assert.Equal("Please wait...", model.ProgressText);
		await wizard.CompleteAsync();
		reporter.Report(new InitializationProgress("After completion", 3, 3));
		Dispatcher.UIThread.RunJobs();
		Assert.False(window.IsVisible);
		log.Close();
	});

	[Fact]
	public Task DesktopCompositionInitializesSessionAndSourcesAndRoutesControllerLogs() => OnUI(async () =>
	{
		var owner = new Window();
		owner.Show();
		var log = new LogViewModel();
		var services = DesktopServices.Create(owner, log,
			new DesktopSettingsStore(Path.Combine(Path.GetTempPath(), $"aissyst-settings-{Guid.NewGuid():N}.json")));
		var database = new TestSessionDatabase();
		var sources = new TestAudioCapture();
		var transcriber = new TestTranscriber();
		var connecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var connected = new TaskCompletionSource<DialogResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
		var connector = new TestLLMConnector { Initialize = async (_, token) =>
		{
			connecting.TrySetResult();
			return await connected.Task.WaitAsync(token);
		} };
		services.AddSingleton<IDatabase>(database);
		services.AddSingleton<ISessionSelector>(provider => new ChoosingSessionSelector(provider.GetRequiredService<IWizardService>()));
		services.AddSingleton<IAudioCapture>(sources);
		services.AddSingleton<ITranscriber>(transcriber);
		services.AddSingleton<ILLMConnector>(connector);
		await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
		var controller = provider.GetRequiredService<ApplicationController>();
		var wizard = Assert.IsType<WizardService>(provider.GetRequiredService<IWizardService>());
		var initialization = Task.Run(() => controller.InitializeAsync(wizard.CancellationToken));
		await Until(() => wizard.Window is { IsVisible: true });
		var window = Assert.IsType<WizardWindow>(wizard.Window);
		var model = Assert.IsType<WizardViewModel>(window.DataContext);
		await Until(() => model.Title == "Select a session" && !model.IsWaiting);
		window.FindControl<Button>("NextButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		await connecting.Task;
		await Until(() => model.ProgressText.Contains("connecting to the LLM", StringComparison.Ordinal));
		Assert.True(model.IsWorking);
		Assert.True(model.IsIndeterminate);
		Assert.False(model.CanNext);
		connected.SetResult(new DialogResponse());
		await Until(() => model.Title == "Select a microphone" && !model.IsWaiting);
		Assert.False(model.IsWorking);
		window.FindControl<Button>("NextButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		await Until(() => model.Title == "Select an audio application" && !model.IsWaiting);
		window.FindControl<Button>("NextButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		await initialization;
		Assert.True(provider.GetRequiredService<SessionModel>().IsReady);
		Assert.Equal(1, connector.SessionInitializations);
		Assert.Equal(1, transcriber.Initializations);
		Assert.Equal(sources.InputDevices[0], provider.GetRequiredService<ICaptureSession>().SelectedMicrophone);
		Assert.Equal(0, sources.MicrophoneCaptures);
		Assert.Equal(0, sources.ApplicationCaptures);
		provider.GetRequiredService<ILogger<ApplicationController>>().LogInformation("Controller diagnostic.");
		Dispatcher.UIThread.RunJobs();
		Assert.Contains(log.Entries, entry => entry.Category == "aissyst.core.Controller.ApplicationController"
			&& entry.Message == "Controller diagnostic.");
		await wizard.CompleteAsync();
		Assert.False(window.IsVisible);
		owner.Close();
	});

	private sealed class ChoosingSessionSelector(IWizardService wizard) : ISessionSelector
	{
		public int GetSelectedSessionId() => 7;
		public async Task InitializeAsync(CancellationToken cancellationToken = default) =>
			await wizard.ShowAsync(new WizardPage("Select a session", "Choose a session.", [new("7", "Session")]) { SelectedKey = "7" }, cancellationToken);
	}

	private async Task OnUI(Func<Task> action)
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
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
}
