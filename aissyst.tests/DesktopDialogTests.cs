using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using aissyst.core;
using aissyst.core.Database;
using aissyst.core.Processor;
using aissyst.desktop;
using aissyst.desktop.Dialog;
using aissyst.desktop.Logging;
using aissyst.desktop.Settings;
using aissyst.desktop.ViewModels;
using aissyst.desktop.Views;
using Microsoft.Extensions.Logging.Abstractions;

namespace aissyst.tests;

[Collection("Desktop UI")]
public sealed class DesktopDialogTests : IDisposable
{
	private readonly HeadlessUnitTestSession _ui = HeadlessUnitTestSession.StartNew(typeof(App));
	private readonly string _directory = Path.Combine(Path.GetTempPath(), $"aissyst-dialog-ui-{Guid.NewGuid():N}");

	[Fact]
	public Task RealTextProcessorUpdatesTheUnfinishedTurnWithoutConsoleDeltaDuplicatesAndReplacesReloadedHistory() => OnUI(async () =>
	{
		var selector = new TestSessionSelector();
		await selector.InitializeAsync();
		var database = new TestSessionDatabase();
		var timestamp = DateTime.UtcNow.AddMinutes(-1);
		database.Session = database.Session with { Dialog = [new(timestamp, "interviewer", "Restored question.")] };
		await using var processor = new TextProcessor(selector, database, NullLogger<TextProcessor>.Instance);
		var coordinator = new DialogCoordinator(processor);
		var model = new DialogViewModel();
		using var output = new DesktopOutput(coordinator, model, NullLogger<DesktopOutput>.Instance);
		await coordinator.InitializeAsync();
		Dispatcher.UIThread.RunJobs();
		Assert.Equal("Restored question.", Assert.Single(model.Entries).Text);
		var completed = 0;
		coordinator.MessageCompleted += _ => completed++;
		await Task.Run(() => coordinator.ProcessAsync(new(timestamp.AddSeconds(1), AudioSourceType.Microphone, "My first")));
		Dispatcher.UIThread.RunJobs();
		Assert.Equal(2, model.Entries.Count);
		var unfinished = model.Entries[^1];
		Assert.Equal("My first", unfinished.Text);
		Assert.Equal(0, completed);
		await coordinator.ProcessAsync(new(timestamp.AddSeconds(2), AudioSourceType.Microphone, "answer."));
		Dispatcher.UIThread.RunJobs();
		Assert.Same(unfinished, model.Entries[^1]);
		Assert.Equal("My first answer.", unfinished.Text);
		Assert.Equal(0, completed);
		await coordinator.ProcessAsync(new(timestamp.AddSeconds(3), AudioSourceType.Application, "Next question."));
		output.PrintDialogMessage(new(timestamp.AddSeconds(2), AudioSourceType.Microphone, "answer."));
		output.PrintMultipleDialogMessages([new(timestamp.AddSeconds(2), AudioSourceType.Microphone, "answer.")]);
		Dispatcher.UIThread.RunJobs();
		Assert.Equal(1, completed);
		Assert.Equal(3, model.Entries.Count);
		Assert.Equal("My first answer.", model.Entries[1].Text);
		Assert.Equal(Brushes.White, model.Entries[0].Foreground);
		Assert.Equal(Brushes.LightGray, model.Entries[1].Foreground);
		Assert.True(model.Entries[1].Spacing.Left > model.Entries[0].Spacing.Left);
		Assert.Contains("Interviewer", model.Entries[0].Header);
		Assert.Contains("Interviewee", model.Entries[1].Header);
		output.PrintLLMAnswer("A suggestion is not spoken dialog.");
		Assert.DoesNotContain(model.GetSnapshot(), message => message.Text.Contains("suggestion"));
		database.Session = database.Session with { Dialog = [new(timestamp, "unknown", "Replacement history.")] };
		await coordinator.ReloadAsync();
		output.Clear();
		Dispatcher.UIThread.RunJobs();
		Assert.Equal("Replacement history.", Assert.Single(model.Entries).Text);
		Assert.Contains("Unknown speaker", model.Entries[0].Header);
		output.Dispose();
		await coordinator.ProcessAsync(new(timestamp.AddSeconds(10), AudioSourceType.Application, "After disposal."));
		Assert.Single(model.GetSnapshot());
	});

	[Fact]
	public Task DialogVirtualizesAllowsReadingHistoryAndAlwaysScrollsForNewOrExtendedMessages() => OnUI(async () =>
	{
		var model = new DialogViewModel();
		var start = DateTime.UtcNow;
		var messages = Enumerable.Range(0, 300).Select(index => new DialogMessage(start.AddSeconds(index),
			index % 2 == 0 ? AudioSourceType.Application : AudioSourceType.Microphone, $"Message {index}.")).ToList();
		model.ReplaceHistory(messages);
		await using var dialogs = new DesktopDialogService(model, NullLogger<DesktopDialogService>.Instance);
		dialogs.Show();
		var window = dialogs.Window!;
		window.Width = 340;
		window.Height = 300;
		var list = window.FindControl<ListBox>("DialogList")!;
		await Until(() => list.GetVisualDescendants().OfType<ScrollViewer>().Any());
		var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().First();
		await Until(() => list.ItemCount == 300 && AtBottom(scroll) && scroll.Extent.Height > scroll.Viewport.Height);
		Assert.IsType<VirtualizingStackPanel>(list.ItemsPanelRoot);
		Assert.InRange(list.GetRealizedContainers().Count(), 1, 100);
		Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), button => button is not RepeatButton);
		scroll.Offset = new Vector(0, 0);
		await Until(() => scroll.Offset.Y == 0);
		await Task.Delay(40);
		Assert.Equal(0, scroll.Offset.Y);
		messages.Add(new(start.AddSeconds(301), AudioSourceType.Application, "New question."));
		model.ReplaceHistory(messages);
		await Until(() => list.ItemCount == 301 && AtBottom(scroll));
		scroll.Offset = new Vector(0, 0);
		await Task.Delay(40);
		Assert.Equal(0, scroll.Offset.Y);
		var lastEntry = model.Entries[^1];
		messages[^1] = messages[^1] with { Text = string.Join(Environment.NewLine, Enumerable.Repeat("Long continuation of the same turn.", 30)) };
		model.ReplaceHistory(messages);
		await Until(() => model.Entries[^1].Text == messages[^1].Text && AtBottom(scroll));
		Assert.Same(lastEntry, model.Entries[^1]);
		window.Close();
		Assert.False(window.IsVisible);
		messages.Add(new(start.AddSeconds(302), AudioSourceType.Microphone, "While hidden."));
		model.ReplaceHistory(messages);
		dialogs.Show();
		Assert.Same(window, dialogs.Window);
		await Until(() => list.ItemCount == 302 && AtBottom(scroll));
	});

	[Fact]
	public Task InterviewMenuWorksFromLogDialogAndNewWindowsAndSavesWithoutAnotherDialogWindow() => OnUI(async () =>
	{
		var model = new DialogViewModel();
		var pickerCalls = 0;
		await using var dialog = new DesktopDialogService(model, NullLogger<DesktopDialogService>.Instance, options =>
		{
			pickerCalls++;
			Assert.Equal("Save Dialog", options.Title);
			Assert.Equal("txt", options.DefaultExtension);
			return Task.FromResult<DialogSaveTarget?>(null);
		});
		var main = new MainWindow { DataContext = new LogViewModel() };
		var wizard = new Window();
		var settings = new DesktopSettingsDialog(new DesktopSettingsStore(Path.Combine(_directory, "settings.json")),
			() => main, () => [main], NullLogger<SettingsWindow>.Instance);
		var shell = new DesktopApplicationShell(Application.Current!, main, settings, () => { },
			NullLogger<DesktopApplicationShell>.Instance, dialog);
		try
		{
			main.Show();
			wizard.Show();
			var root = NativeMenu.GetMenu(wizard)!;
			var interview = Assert.IsType<NativeMenuItem>(Assert.Single(root.Items));
			Assert.Equal("Interview", interview.Header);
			((INativeMenuItemExporterEventsImplBridge)interview.Menu!.Items[0]).RaiseClicked();
			var window = dialog.Window!;
			Assert.True(window.IsVisible);
			Assert.Empty(main.OwnedWindows);
			Assert.Empty(wizard.OwnedWindows);
			var dialogMenu = Assert.IsType<NativeMenuItem>(NativeMenu.GetMenu(window)!.Items[0]).Menu!;
			Assert.Equal("Save Dialog…", Assert.IsType<NativeMenuItem>(dialogMenu.Items[1]).Header);
			((INativeMenuItemExporterEventsImplBridge)dialogMenu.Items[1]).RaiseClicked();
			Assert.Equal(1, pickerCalls);
			Assert.Same(window, dialog.Window);
			window.Close();
			Assert.False(window.IsVisible);
			((INativeMenuItemExporterEventsImplBridge)interview.Menu.Items[0]).RaiseClicked();
			Assert.Same(window, dialog.Window);
			Assert.True(window.IsVisible);
		}
		finally
		{
			shell.PrepareShutdown();
			shell.AllowClose();
			main.Close();
			wizard.Close();
		}
		await Task.CompletedTask;
	});

	[Fact]
	public Task SaveUsesAClickTimeSnapshotWritesUtf8AndTruncatesAnExistingFile() => OnUI(async () =>
	{
		Directory.CreateDirectory(_directory);
		var path = Path.Combine(_directory, "dialog.txt");
		await File.WriteAllTextAsync(path, new string('x', 10_000));
		var model = new DialogViewModel();
		DialogMessage[] original = [new(DateTime.UtcNow, AudioSourceType.Application, "Вопрос\nwith two lines."),
			new(DateTime.UtcNow.AddSeconds(1), AudioSourceType.Microphone, "Ответ.")];
		model.ReplaceHistory(original);
		var picker = new TaskCompletionSource<DialogSaveTarget?>(TaskCreationOptions.RunContinuationsAsynchronously);
		var pickerCalls = 0;
		var file = new TestSaveFile(path);
		await using var dialog = new DesktopDialogService(model, NullLogger<DesktopDialogService>.Instance, options =>
		{
			pickerCalls++;
			Assert.EndsWith(".txt", options.SuggestedFileName);
			Assert.Contains(FilePickerFileTypes.TextPlain, options.FileTypeChoices!);
			return picker.Task;
		});
		var save = dialog.SaveAsync();
		Assert.Same(save, dialog.SaveAsync());
		Assert.Equal(1, pickerCalls);
		model.ReplaceHistory([.. original, new(DateTime.UtcNow.AddSeconds(2), AudioSourceType.Application, "Later message.")]);
		picker.SetResult(file.CreateTarget());
		await save;
		Assert.Equal(DialogViewModel.Format(original), await File.ReadAllTextAsync(path));
		Assert.DoesNotContain("Later message.", await File.ReadAllTextAsync(path));
		var bytes = await File.ReadAllBytesAsync(path);
		Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
		Assert.Contains("Interviewer:", Encoding.UTF8.GetString(bytes));
		Assert.Contains("Interviewee:", Encoding.UTF8.GetString(bytes));
		Assert.True(file.Disposed);
	});

	[Fact]
	public Task CancelAndWriteFailureDoNotChangeHistoryAndAllowAnotherSave() => OnUI(async () =>
	{
		var model = new DialogViewModel();
		model.ReplaceHistory([new(DateTime.UtcNow, AudioSourceType.Application, "Keep this message.")]);
		var calls = 0;
		var file = new TestSaveFile(Path.Combine(_directory, "missing-parent", "dialog.txt"));
		await using var dialog = new DesktopDialogService(model, NullLogger<DesktopDialogService>.Instance, _ =>
			Task.FromResult<DialogSaveTarget?>(++calls == 2 ? file.CreateTarget() : null));
		await dialog.SaveAsync();
		Assert.False(Directory.Exists(_directory));
		await Assert.ThrowsAnyAsync<IOException>(() => dialog.SaveAsync());
		Assert.True(file.Disposed);
		await dialog.SaveAsync();
		Assert.Equal(3, calls);
		Assert.Equal("Keep this message.", Assert.Single(model.GetSnapshot()).Text);
	});

	[Fact]
	public Task ShutdownCancelsThePickerWaitAndDisposesALatePickerResult() => OnUI(async () =>
	{
		var picker = new TaskCompletionSource<DialogSaveTarget?>(TaskCreationOptions.RunContinuationsAsynchronously);
		var dialog = new DesktopDialogService(new DialogViewModel(), NullLogger<DesktopDialogService>.Instance, _ => picker.Task);
		var save = dialog.SaveAsync();
		var window = dialog.Window!;
		await dialog.DisposeAsync();
		Assert.False(window.IsVisible);
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => save);
		var file = new TestSaveFile(Path.Combine(_directory, "unused.txt"));
		picker.SetResult(file.CreateTarget());
		await Until(() => file.Disposed);
		Assert.False(Directory.Exists(_directory));
	});

	private sealed class TestSaveFile(string filePath)
	{
		public bool Disposed { get; private set; }
		public DialogSaveTarget CreateTarget() => new(Path.GetFileName(filePath),
			() => Task.FromResult<Stream>(new FileStream(filePath, FileMode.OpenOrCreate, FileAccess.Write)),
			() => Disposed = true);
	}

	private static bool AtBottom(ScrollViewer scroll) => scroll.Extent.Height - scroll.Viewport.Height - scroll.Offset.Y <= 1;
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
