using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using aissyst.desktop;
using aissyst.desktop.Logging;
using aissyst.desktop.ViewModels;
using aissyst.desktop.Views;
using Microsoft.Extensions.Logging;

namespace aissyst.tests;

[Collection("Desktop UI")]
public sealed class DesktopLogTests : IDisposable
{
	private readonly HeadlessUnitTestSession _ui = HeadlessUnitTestSession.StartNew(typeof(App));
	public void Dispose() => _ui.Dispose();

	[Fact]
	public Task FiltersKeepCompleteEntriesAndCopyIncludesHiddenAndPendingMessages() => OnUI(async () =>
	{
		var log = new LogViewModel();
		var window = new MainWindow { DataContext = log };
		window.Show();
		try
		{
			using var provider = new WindowLoggerProvider(log.Append);
			var logger = provider.CreateLogger("Test.Category");
			logger.LogInformation("Информация: ready.");
			logger.LogWarning("Warning message.");
			logger.LogError(new InvalidOperationException("Failure details."), "Error message.");
			logger.LogCritical("Critical message.");
			Dispatcher.UIThread.RunJobs();
			Assert.Equal(4, log.Entries.Count);
			Assert.Equal(Brushes.White, log.Entries[0].Foreground);
			Assert.Equal(Brushes.Yellow, log.Entries[1].Foreground);
			Assert.Equal(Brushes.Red, log.Entries[2].Foreground);
			Assert.Equal(Brushes.Red, log.Entries[3].Foreground);
			Assert.Contains("Failure details.", log.Entries[2].Text);
			Assert.Contains(Environment.NewLine, log.Entries[2].Text);
			window.FindControl<ToggleButton>("InfoFilter")!.IsChecked = false;
			window.FindControl<ToggleButton>("WarningFilter")!.IsChecked = false;
			Assert.Equal([LogLevel.Error, LogLevel.Critical], log.Entries.Select(entry => entry.Level));
			logger.LogInformation("Hidden and not yet displayed.");
			var expected = LogViewModel.Format(log.GetSnapshot());
			var copy = window.FindControl<Button>("CopyButton")!;
			copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			Assert.False(copy.IsEnabled);
			await Until(() => copy.IsEnabled);
			Assert.Equal(expected, await window.Clipboard!.TryGetTextAsync());
			Assert.Equal(2, log.Entries.Count);
			window.FindControl<ToggleButton>("ErrorFilter")!.IsChecked = false;
			Assert.Empty(log.Entries);
			window.FindControl<ToggleButton>("InfoFilter")!.IsChecked = true;
			Assert.Equal(2, log.Entries.Count);
			window.FindControl<ToggleButton>("WarningFilter")!.IsChecked = true;
			window.FindControl<ToggleButton>("ErrorFilter")!.IsChecked = true;
			Assert.Equal(log.GetSnapshot(), log.Entries);
		}
		finally
		{
			window.Close();
		}
	});

	[Fact]
	public Task ClearDiscardsHiddenAndQueuedMessagesAndAcceptsNewMessages() => OnUI(async () =>
	{
		var log = new LogViewModel();
		var window = new MainWindow { DataContext = log };
		window.Show();
		try
		{
			log.Append(Entry("Visible before clear."));
			Dispatcher.UIThread.RunJobs();
			log.ShowInfo = false;
			Task.Run(() => log.Append(Entry("Queued before clear."))).GetAwaiter().GetResult();
			window.FindControl<Button>("ClearButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			Assert.Empty(log.GetSnapshot());
			log.Append(Entry("After clear.", LogLevel.Warning));
			Dispatcher.UIThread.RunJobs();
			Assert.Equal("After clear.", Assert.Single(log.Entries).Message);
			log.ShowInfo = true;
			Assert.Single(log.Entries);
			Assert.Equal(log.GetSnapshot(), log.Entries);
			log.Clear();
			var copy = window.FindControl<Button>("CopyButton")!;
			copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			await Until(() => copy.IsEnabled);
			Assert.True(string.IsNullOrEmpty(await window.Clipboard!.TryGetTextAsync()));
		}
		finally
		{
			window.Close();
		}
	});

	[Fact]
	public Task BackgroundMessagesAreBatchedWithoutTruncationOrDuplicatesAcrossFilterChanges() => OnUI(() =>
	{
		var log = new LogViewModel();
		var notifications = 0;
		((INotifyCollectionChanged)log.Entries).CollectionChanged += (_, _) => notifications++;
		Task.Run(() => Parallel.For(0, 2_000, i => log.Append(Entry($"Entry {i}.")))).GetAwaiter().GetResult();
		var longMessage = new string('x', 250_000);
		log.Append(Entry(longMessage));
		Assert.Empty(log.Entries);
		Assert.Equal(2_001, log.GetSnapshot().Length);
		Dispatcher.UIThread.RunJobs();
		Assert.Equal(1, notifications);
		Assert.Equal(2_001, log.Entries.Count);
		Assert.Equal(longMessage, log.Entries[^1].Message);
		Assert.Equal(2_001, log.Entries.Distinct().Count());
		log.Append(Entry("Queued during filter change."));
		log.ShowWarning = false;
		Dispatcher.UIThread.RunJobs();
		Assert.Equal(2_002, log.Entries.Count);
		Assert.Equal(log.GetSnapshot(), log.Entries);
		return Task.CompletedTask;
	});

	[Fact]
	public Task VirtualizedLogPausesWhileReadingAndLatestResumesFollowingIncludingMultilineEntries() => OnUI(async () =>
	{
		var log = new LogViewModel();
		for (var i = 0; i < 300; i++)
		{
			log.Append(Entry($"Entry {i}."));
		}
		var window = new MainWindow { DataContext = log, Width = 640, Height = 320 };
		window.Show();
		try
		{
			var list = window.FindControl<ListBox>("LogList")!;
			var latest = window.FindControl<Button>("LatestButton")!;
			await Until(() => list.GetVisualDescendants().OfType<ScrollViewer>().Any());
			var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().First();
			await Until(() => AtBottom(scroll) && scroll.Extent.Height > scroll.Viewport.Height);
			Assert.IsType<VirtualizingStackPanel>(list.ItemsPanelRoot);
			Assert.InRange(list.GetRealizedContainers().Count(), 1, 100);
			Assert.False(latest.IsVisible);
			scroll.Offset = new Vector(0, 0);
			await Until(() => latest.IsVisible);
			var offset = scroll.Offset;
			log.Append(Entry("New message while reading history."));
			await Until(() => list.ItemCount == 301);
			await Task.Delay(30);
			Assert.Equal(offset, scroll.Offset);
			Assert.True(latest.IsVisible);
			latest.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			await Until(() => AtBottom(scroll) && !latest.IsVisible);
			log.Append(Entry(string.Join(Environment.NewLine, Enumerable.Range(0, 40).Select(i => $"Exception line {i}.")), LogLevel.Error));
			await Until(() => list.ItemCount == 302 && AtBottom(scroll));
			Assert.False(latest.IsVisible);
			scroll.Offset = new Vector(0, 0);
			await Until(() => latest.IsVisible);
			scroll.ScrollToEnd();
			await Until(() => !latest.IsVisible);
			log.Append(Entry("Automatic following restored by manual scroll."));
			await Until(() => list.ItemCount == 303 && AtBottom(scroll));
			scroll.Offset = new Vector(0, 0);
			await Until(() => latest.IsVisible);
			window.FindControl<Button>("ClearButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			Assert.False(latest.IsVisible);
			log.Append(Entry("First entry after clear."));
			await Until(() => list.ItemCount == 1 && AtBottom(scroll));
		}
		finally
		{
			window.Close();
		}
	});

	private static LogEntry Entry(string message, LogLevel level = LogLevel.Information) =>
		new(DateTimeOffset.Now, level, "Test.Category", message, null);

	private static bool AtBottom(ScrollViewer scroll) =>
		scroll.Extent.Height - scroll.Viewport.Height - scroll.Offset.Y <= 1;

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
}
