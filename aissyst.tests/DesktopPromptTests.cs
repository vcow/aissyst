using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Avalonia.Platform;
using System.Reflection;
using aissyst.core.Audio;
using aissyst.core.LLMConnector.DTO;
using aissyst.core.LLMConnector.Model;
using aissyst.desktop;
using aissyst.desktop.Interview;
using aissyst.desktop.Settings;
using aissyst.desktop.ViewModels;
using aissyst.desktop.Views;
using ColorTextBlock.Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Fixture = aissyst.tests.DesktopInterviewTests.Fixture;

namespace aissyst.tests;

[Collection("Desktop UI")]
public sealed class DesktopPromptTests : IDisposable
{
	private readonly HeadlessUnitTestSession _ui = HeadlessUnitTestSession.StartNew(typeof(App));

	[Fact]
	public Task PromptOpensImmediatelyAndSharesAllSixCommandsWithNativeMenus() => OnUI(async () =>
	{
		await using var fixture = new Fixture();
		var window = fixture.Prompt.Window!;
		var model = fixture.Provider.GetRequiredService<PromptViewModel>();
		Assert.True(window.IsVisible);
		Assert.True(window.Topmost);
		Assert.All(fixture.Commands.Items, command => Assert.False(command.CanExecute(null)));
		Assert.False(Find(fixture.Main, "Interview", "Translate").IsEnabled);
		await fixture.InitializeAsync();
		await fixture.ReadyAsync();
		await Until(() => fixture.Commands.Items[1].CanExecute(null));
		window.UpdateLayout();
		var buttons = window.GetLogicalDescendants().OfType<Button>()
			.Where(button => button.Command is InterviewCommand).ToArray();
		Assert.Equal(6, buttons.Length);
		for (var index = 0; index < 6; index++)
		{
			var command = fixture.Commands.Items[index];
			Assert.Same(command, buttons[index].Command);
			Assert.Same(command, Find(fixture.Main, "Interview", command.Caption).Command);
			Assert.Equal(command.Gesture, Find(window, "Interview", command.Caption).Gesture);
		}
		Assert.False(buttons[0].IsEffectivelyEnabled);
		Assert.All(buttons.Skip(1), button => Assert.True(button.IsEffectivelyEnabled));
		var requests = new List<RequestType>();
		fixture.Connector.Send = (request, _) =>
		{
			requests.Add(request.Request);
			return Task.FromResult(Response(request, $"**{request.Request}**"));
		};
		foreach (var command in fixture.Commands.Items.Skip(1))
		{
			Click(Find(window, "Interview", command.Caption));
			await fixture.Commands.WaitForIdleAsync();
			await Until(() => model.Markdown == $"**{command.Request}**");
		}
		Assert.Equal(new[] { RequestType.Translate, RequestType.Advice, RequestType.Reply,
			RequestType.TranslateAndReply, RequestType.TranslateAndAdvice }, requests);
		Assert.False(model.IsProcessing);
		Assert.DoesNotContain(((LogViewModel)fixture.Main.DataContext!).GetSnapshot(), entry => entry.Message == model.Markdown);
		window.Close();
		Assert.False(window.IsVisible);
		Assert.False(Find(fixture.Main, "Windows", "Prompt").IsChecked);
		Click(Find(fixture.Main, "Windows", "Prompt"));
		Assert.True(window.IsVisible);
		Assert.Same(window, fixture.Prompt.Window);
	});

	[Fact]
	public Task BusyIndicatorCoversTranscriptionAndDuplicateDoesNotClearItOrThePreviousAnswer() => OnUI(async () =>
	{
		await using var fixture = new Fixture();
		await StartAsync(fixture);
		var model = fixture.Provider.GetRequiredService<PromptViewModel>();
		model.ShowAnswer("Previous prompt");
		var transcribing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var transcript = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
		var sent = new TaskCompletionSource<DialogRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
		var answer = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
		fixture.Transcriber.Recognize = async (_, token) =>
		{
			transcribing.TrySetResult();
			return await transcript.Task.WaitAsync(token);
		};
		fixture.Connector.Send = async (request, token) =>
		{
			sent.TrySetResult(request);
			return Response(request, await answer.Task.WaitAsync(token));
		};
		try
		{
			await fixture.Audio.EmitApplicationAsync(Frame());
			var first = fixture.Commands.ExecuteAsync(fixture.Commands.Items[1]);
			await transcribing.Task.WaitAsync(TimeSpan.FromSeconds(5));
			Assert.True(model.IsProcessing);
			Assert.True(fixture.Prompt.Window!.FindControl<StackPanel>("ProcessingIndicator")!.IsVisible);
			Assert.Equal("Previous prompt", model.Markdown);
			Assert.All(fixture.Commands.Items, command => Assert.True(command.CanExecute(null)));
			await fixture.Commands.ExecuteAsync(fixture.Commands.Items[2]);
			Assert.True(model.IsProcessing);
			Assert.Equal(0, fixture.Connector.Requests);
			Assert.Contains(((LogViewModel)fixture.Main.DataContext!).GetSnapshot(), entry => entry.Level == LogLevel.Error);
			transcript.TrySetResult("First question");
			var request = await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
			Assert.Contains(request.RawRecords, record => record.Message == "First question");
			await fixture.Commands.ExecuteAsync(fixture.Commands.Items[0]).WaitAsync(TimeSpan.FromSeconds(5));
			Assert.True(model.IsProcessing);
			Assert.Equal(1, fixture.Connector.Requests);
			answer.TrySetResult("# New prompt\n\nA **formatted** answer.");
			await first;
			await Until(() => model.Markdown.StartsWith("# New prompt"));
			Assert.False(model.IsProcessing);
			fixture.Connector.Send = (_, _) => throw new IOException("Test request failure");
			await fixture.Commands.ExecuteAsync(fixture.Commands.Items[3]);
			Assert.StartsWith("# New prompt", model.Markdown);
			Assert.False(model.IsProcessing);
		}
		finally
		{
			transcript.TrySetResult("First question");
			answer.TrySetResult("Finished");
		}
	});

	[Fact]
	public Task DisconnectDisablesCommandsUntilSessionRecoveryCompletes() => OnUI(async () =>
	{
		await using var fixture = new Fixture();
		await fixture.InitializeAsync();
		await fixture.ReadyAsync();
		var restore = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		fixture.Connector.Initialize = async (_, token) =>
		{
			await restore.Task.WaitAsync(token);
			return new DialogResponse();
		};
		fixture.Connector.Disconnect();
		await Until(() => !Find(fixture.Main, "Interview", "Translate").IsEnabled);
		Assert.All(fixture.Commands.Items, command => Assert.False(command.CanExecute(null)));
		Assert.False(fixture.Prompt.Window!.GetLogicalDescendants().OfType<Button>()
			.Single(button => button.Content as string == "Translate").IsEffectivelyEnabled);
		restore.TrySetResult();
		await Until(() => Find(fixture.Main, "Interview", "Translate").IsEnabled);
		Assert.True(fixture.Provider.GetRequiredService<SessionModel>().IsReady);
	});

	[Fact]
	public Task SavedHotkeysAndFontApplyLiveAndKeysAreIgnoredInInactiveOrModalWindows() => OnUI(async () =>
	{
		await using var fixture = new Fixture();
		await fixture.InitializeAsync();
		await fixture.ReadyAsync();
		fixture.Connector.Send = (request, _) => Task.FromResult(Response(request, "Translated"));
		var prompt = fixture.Prompt.Window!;
		fixture.Main.Activate();
		SetActive(fixture.Main, true);
		SetActive(prompt, false);
		RaiseKey(prompt, Key.D1, KeyModifiers.Control | KeyModifiers.Alt);
		Assert.Equal(0, fixture.Connector.Requests);
		RaiseKey(fixture.Main, Key.D1, KeyModifiers.Control | KeyModifiers.Alt);
		await fixture.Commands.WaitForIdleAsync();
		Assert.Equal(1, fixture.Connector.Requests);
		var changed = fixture.Settings.Saved with
		{
			Prompt = new PromptSettings { FontSize = 24, Translate = "Ctrl+Alt+T" }
		};
		await fixture.Settings.SaveAsync(changed);
		Assert.Equal(24, fixture.Provider.GetRequiredService<PromptViewModel>().FontSize);
		Assert.Equal(24d, prompt.Viewer.Resources["ControlContentThemeFontSize"]);
		Assert.Equal(KeyGesture.Parse("Ctrl+Alt+T"), Find(fixture.Main, "Interview", "Translate").Gesture);
		fixture.Main.Activate();
		RaiseKey(fixture.Main, Key.D1, KeyModifiers.Control | KeyModifiers.Alt);
		Assert.Equal(1, fixture.Connector.Requests);
		RaiseKey(fixture.Main, Key.T, KeyModifiers.Control | KeyModifiers.Alt);
		await fixture.Commands.WaitForIdleAsync();
		Assert.Equal(2, fixture.Connector.Requests);
		Click((NativeMenuItem)NativeMenu.GetMenu(Application.Current!)!.Items[0]);
		var settings = fixture.SettingsDialog.Window!;
		Assert.All(fixture.Commands.Items, command => Assert.False(command.CanExecute(null)));
		settings.FindControl<TabControl>("SettingsTabs")!.SelectedIndex = 4;
		settings.UpdateLayout();
		var editor = settings.GetLogicalDescendants().OfType<HotkeyTextBox>().ElementAt(1);
		RaiseKey(editor, Key.Y, KeyModifiers.Control | KeyModifiers.Alt);
		Assert.Equal("Ctrl+Alt+Y", ((SettingsViewModel)settings.DataContext!).Hotkeys[1].Gesture);
		Assert.Equal(2, fixture.Connector.Requests);
		RaiseKey(settings, Key.T, KeyModifiers.Control | KeyModifiers.Alt);
		Assert.Equal(2, fixture.Connector.Requests);
		fixture.SettingsDialog.Close();
		await fixture.SettingsDialog.WaitForCloseAsync();
		Assert.Equal(KeyGesture.Parse("Ctrl+Alt+T"), fixture.Commands.Items[1].Gesture);
		Assert.Equal(changed.Prompt, new DesktopSettingsStore(fixture.Settings.FilePath).Active.Prompt);
	});

	[Fact]
	public Task HotkeyFieldsHaveVisibleTemplatesAndCaptureKeysThroughKeyboardFocus() => OnUI(async () =>
	{
		await using var fixture = new Fixture();
		Click((NativeMenuItem)NativeMenu.GetMenu(Application.Current!)!.Items[0]);
		var window = fixture.SettingsDialog.Window!;
		window.FindControl<TabControl>("SettingsTabs")!.SelectedIndex = 4;
		window.UpdateLayout();
		var editors = window.GetVisualDescendants().OfType<HotkeyTextBox>().ToArray();
		Assert.Equal(6, editors.Length);
		foreach (var editor in editors)
		{
			Assert.NotNull(editor.Template);
			Assert.True(editor.Bounds.Width > 0 && editor.Bounds.Height > 0);
			var presenter = Assert.Single(editor.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.TextPresenter>());
			Assert.Equal(editor.Text, presenter.Text);
			Assert.True(presenter.Bounds.Width > 0 && presenter.Bounds.Height > 0);
		}
		var target = editors[1];
		Assert.True(target.Focus());
		window.KeyPress(Key.Y, RawInputModifiers.Control | RawInputModifiers.Alt, PhysicalKey.Y, "y");
		window.KeyRelease(Key.Y, RawInputModifiers.Control | RawInputModifiers.Alt, PhysicalKey.Y, "y");
		Assert.Equal("Ctrl+Alt+Y", ((SettingsViewModel)window.DataContext!).Hotkeys[1].Gesture);
		window.KeyPress(Key.Back, RawInputModifiers.None, PhysicalKey.Backspace, null);
		window.KeyRelease(Key.Back, RawInputModifiers.None, PhysicalKey.Backspace, null);
		Assert.Equal(string.Empty, target.Text);
		fixture.SettingsDialog.Close();
		await fixture.SettingsDialog.WaitForCloseAsync();
	});

	[Fact]
	public Task MarkdownRendersAndFontChangeUpdatesParagraphsAndRelativeHeadings() => OnUI(async () =>
	{
		await using var fixture = new Fixture();
		var prompt = fixture.Prompt.Window!;
		var model = fixture.Provider.GetRequiredService<PromptViewModel>();
		model.ShowAnswer("# Heading\n\nA **bold** paragraph.\n\n- First\n- Second\n\n| A | B |\n|---|---|\n| 1 | 2 |\n\n```cs\nvar number = 1;\n```\n");
		prompt.UpdateLayout();
		var texts = prompt.Viewer.GetVisualDescendants().OfType<CTextBlock>().ToArray();
		Assert.NotEmpty(texts);
		var heading = texts.Single(text => text.Classes.Contains("Heading1"));
		var paragraph = texts.First(text => !text.Classes.Contains("Heading1") && text.FontSize == 18);
		Assert.Equal(18 * 3.2, heading.FontSize, 4);
		var editor = Assert.Single(prompt.Viewer.GetVisualDescendants().OfType<AvaloniaEdit.TextEditor>());
		Assert.Equal(18, editor.FontSize);
		await fixture.Settings.SaveAsync(fixture.Settings.Saved with { Prompt = new PromptSettings { FontSize = 22 } });
		prompt.UpdateLayout();
		Assert.Equal(22, paragraph.FontSize);
		Assert.Equal(22 * 3.2, heading.FontSize, 4);
		Assert.Equal(22, editor.FontSize);
		model.ShowAnswer("Replacement");
		prompt.UpdateLayout();
		Assert.Equal("Replacement", prompt.Viewer.Markdown);
		Assert.Equal(Vector.Zero, prompt.Viewer.ScrollValue);
	});

	[Fact]
	public Task SettingsValidateHotkeyDuplicatesAndApplyOnlyOnSave() => OnUI(async () =>
	{
		await using var fixture = new Fixture();
		var model = fixture.Provider.GetRequiredService<PromptViewModel>();
		Click((NativeMenuItem)NativeMenu.GetMenu(Application.Current!)!.Items[0]);
		var window = fixture.SettingsDialog.Window!;
		var form = (SettingsViewModel)window.DataContext!;
		form.PromptFontSize = 26;
		form.Hotkeys[1].Gesture = form.Hotkeys[2].Gesture;
		ClickButton(window, "SaveSettingsButton");
		Assert.True(form.HasError);
		Assert.True(window.IsVisible);
		Assert.Equal(18, model.FontSize);
		form.Hotkeys[1].Gesture = "Ctrl+Alt+T";
		ClickButton(window, "SaveSettingsButton");
		await fixture.SettingsDialog.WaitForCloseAsync();
		Assert.Equal(26, model.FontSize);
		Assert.Equal(KeyGesture.Parse("Ctrl+Alt+T"), fixture.Commands.Items[1].Gesture);
		Click((NativeMenuItem)NativeMenu.GetMenu(Application.Current!)!.Items[0]);
		window = fixture.SettingsDialog.Window!;
		ClickButton(window, "RestoreDefaultsButton");
		Assert.Equal(26, model.FontSize);
		ClickButton(window, "SaveSettingsButton");
		await fixture.SettingsDialog.WaitForCloseAsync();
		Assert.Equal(new PromptSettings(), fixture.Settings.Saved.Prompt);
		Assert.Equal(18, model.FontSize);
	});

	[Fact]
	public Task ShutdownCancelsTheActiveRequestAndClearsProcessing() => OnUI(async () =>
	{
		await using var fixture = new Fixture();
		await fixture.InitializeAsync();
		await fixture.ReadyAsync();
		var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		fixture.Connector.Send = async (_, token) =>
		{
			sent.TrySetResult();
			await Task.Delay(Timeout.InfiniteTimeSpan, token);
			return new DialogResponse();
		};
		var request = fixture.Commands.ExecuteAsync(fixture.Commands.Items[1]);
		await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
		fixture.Shell.PrepareShutdown();
		await fixture.Shell.WaitForCommandsAsync().WaitAsync(TimeSpan.FromSeconds(5));
		await request;
		Assert.False(fixture.Provider.GetRequiredService<PromptViewModel>().IsProcessing);
		Assert.All(fixture.Commands.Items, command => Assert.False(command.CanExecute(null)));
	});

	[Fact]
	public void PromptSettingsRejectDuplicateReservedAndInvalidValues()
	{
		Assert.Throws<ArgumentException>(() => (new PromptSettings { Translate = "Ctrl+Alt+D2" }).Validated());
		Assert.Throws<ArgumentException>(() => (new PromptSettings { Translate = "Cmd+Q" }).Validated());
		Assert.Throws<ArgumentException>(() => (new PromptSettings { Translate = "T" }).Validated());
		Assert.Throws<ArgumentException>(() => (new PromptSettings { FontSize = double.NaN }).Validated());
		Assert.Throws<ArgumentException>(() => (new PromptSettings { FontSize = 73 }).Validated());
		(new PromptSettings { Translate = "" }).Validated();
	}

	private static async Task StartAsync(Fixture fixture)
	{
		await fixture.InitializeAsync();
		await fixture.ReadyAsync();
		fixture.Wizard.Window!.FindControl<Button>("NextButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		await fixture.Starting!;
		await fixture.Wizard.CompleteAsync();
	}
	private static DialogResponse Response(DialogRequest request, string message) => new()
	{
		RequestId = request.RequestId, Message = message,
		Summary = request.RawRecords.Count > 0 ? "Dialog updated" : request.Summary,
		SummaryTimestamp = request.RawRecords.LastOrDefault()?.Timestamp
	};
	private static AudioFrame Frame() => new(Enumerable.Repeat(0.5f, 80).ToArray(), new AudioFormat(16000, 1), TimeSpan.Zero);
	private static void RaiseKey(Control target, Key key, KeyModifiers modifiers) => target.RaiseEvent(new KeyEventArgs
	{
		RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers
	});
	private static void SetActive(Window window, bool active)
	{
		// The headless backend has no OS activation callback; simulate that platform notification.
		var property = typeof(IWindowBaseImpl).GetProperty(active ? "Activated" : "Deactivated",
			BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
		((Action?)property.GetValue(window.PlatformImpl))?.Invoke();
	}
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
	private static void Click(NativeMenuItem item) => ((INativeMenuItemExporterEventsImplBridge)item).RaiseClicked();
	private static void ClickButton(Window window, string name) => window.FindControl<Button>(name)!
		.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
	private async Task OnUI(Func<Task> action)
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
		await _ui.Dispatch(async () => { await action(); return true; }, timeout.Token);
	}
	private static async Task Until(Func<bool> condition)
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		while (!condition()) { await Task.Delay(10, timeout.Token); }
	}
	public void Dispose() => _ui.Dispose();
}
