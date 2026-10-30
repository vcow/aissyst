using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using aissyst.desktop.Logging;
using aissyst.desktop.ViewModels;
using Microsoft.Extensions.Logging;

namespace aissyst.desktop.Views;

public partial class MainWindow : Window
{
	internal event Action? SettingsRequested;
	internal event Action? QuitRequested;
	private readonly ListBox _list;
	private readonly Button _latest;
	private LogViewModel? _log;
	private ScrollViewer? _scroll;
	private bool _followingTail = true;
	private bool _tailQueued;

	public MainWindow()
	{
		InitializeComponent();
		_list = this.FindControl<ListBox>("LogList")!;
		_latest = this.FindControl<Button>("LatestButton")!;
		DataContextChanged += (_, _) => AttachLog();
		_list.TemplateApplied += (_, _) => AttachScroll();
		Opened += (_, _) =>
		{
			AttachScroll();
			QueueScrollToLatest();
		};
		Closed += (_, _) =>
		{
			if (_log != null)
			{
				((INotifyCollectionChanged)_log.Entries).CollectionChanged -= EntriesChanged;
			}
			if (_scroll != null)
			{
				_scroll.ScrollChanged -= ScrollChanged;
			}
		};
	}

	private void AttachLog()
	{
		if (_log != null)
		{
			((INotifyCollectionChanged)_log.Entries).CollectionChanged -= EntriesChanged;
		}
		_log = DataContext as LogViewModel;
		if (_log != null)
		{
			((INotifyCollectionChanged)_log.Entries).CollectionChanged += EntriesChanged;
		}
		FollowTail(true);
		QueueScrollToLatest();
	}

	private void AttachScroll()
	{
		if (_scroll != null)
		{
			_scroll.ScrollChanged -= ScrollChanged;
		}
		_scroll = _list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
		if (_scroll != null)
		{
			_scroll.ScrollChanged += ScrollChanged;
		}
	}

	private bool IsAtBottom => _scroll == null
		|| _scroll.Extent.Height - _scroll.Viewport.Height - _scroll.Offset.Y <= 1;

	private void EntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
	{
		if (_log?.Entries.Count == 0)
		{
			FollowTail(true);
		}
		QueueScrollToLatest();
	}

	private void ScrollChanged(object? sender, ScrollChangedEventArgs e)
	{
		if (e.OffsetDelta.Y < 0 && !IsAtBottom)
		{
			FollowTail(false);
		}
		else if (e.ExtentDelta.Y != 0 || e.ViewportDelta.Y != 0)
		{
			// New content must not turn following off before the next scroll runs.
			if (!_followingTail && IsAtBottom)
			{
				FollowTail(true);
			}
			QueueScrollToLatest();
		}
		else if (e.OffsetDelta.Y != 0)
		{
			FollowTail(IsAtBottom);
		}
	}

	private void FollowTail(bool follow)
	{
		_followingTail = follow;
		_latest.IsVisible = !follow;
	}

	private void QueueScrollToLatest()
	{
		if (!_followingTail || _tailQueued || _log?.Entries.Count is not > 0)
		{
			return;
		}
		_tailQueued = true;
		Dispatcher.UIThread.Post(() =>
		{
			_tailQueued = false;
			if (_followingTail && IsVisible && _log?.Entries.Count > 0)
			{
				_list.ScrollIntoView(_log.Entries[^1]);
				_scroll?.ScrollToEnd();
			}
		}, DispatcherPriority.Background);
	}

	private void GoToLatest(object? sender, RoutedEventArgs e)
	{
		FollowTail(true);
		QueueScrollToLatest();
	}

	private void ClearLog(object? sender, RoutedEventArgs e) => _log?.Clear();
	private void OpenSettings(object? sender, RoutedEventArgs e) => SettingsRequested?.Invoke();
	private void QuitApplication(object? sender, RoutedEventArgs e) => QuitRequested?.Invoke();
	private void ShowLog(object? sender, RoutedEventArgs e) => Activate();

	private async void CopyLog(object? sender, RoutedEventArgs e)
	{
		if (_log == null)
		{
			return;
		}
		var log = _log;
		var button = this.FindControl<Button>("CopyButton")!;
		button.IsEnabled = false;
		try
		{
			var snapshot = log.GetSnapshot();
			var text = await Task.Run(() => LogViewModel.Format(snapshot));
			var clipboard = Clipboard ?? throw new InvalidOperationException("The clipboard is unavailable.");
			await clipboard.SetTextAsync(text);
		}
		catch (Exception exception)
		{
			log.Append(new LogEntry(DateTimeOffset.Now, LogLevel.Error, typeof(MainWindow).FullName!,
				"Failed to copy the log to the clipboard.", exception.ToString()));
		}
		finally
		{
			button.IsEnabled = true;
		}
	}
}
