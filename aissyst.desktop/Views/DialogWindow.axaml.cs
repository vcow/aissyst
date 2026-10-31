using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using aissyst.desktop.ViewModels;

namespace aissyst.desktop.Views;

public partial class DialogWindow : Window
{
	private readonly ListBox _list;
	private DialogViewModel? _model;
	private ScrollViewer? _scroll;
	private bool _scrollQueued;
	private bool _closed;
	private bool _followingTail = true;

	public DialogWindow()
	{
		InitializeComponent();
		_list = this.FindControl<ListBox>("DialogList")!;
		DataContextChanged += (_, _) => AttachModel();
		_list.TemplateApplied += (_, _) => AttachScroll();
		Opened += (_, _) =>
		{
			WindowStartupLocation = WindowStartupLocation.Manual;
			AttachScroll();
			DialogUpdated();
		};
		Closed += (_, _) =>
		{
			_closed = true;
			if (_model != null)
			{
				_model.Updated -= DialogUpdated;
			}
			if (_scroll != null)
			{
				_scroll.ScrollChanged -= ContentSizeChanged;
			}
		};
	}

	private void AttachModel()
	{
		if (_model != null)
		{
			_model.Updated -= DialogUpdated;
		}
		_model = DataContext as DialogViewModel;
		if (_model != null)
		{
			_model.Updated += DialogUpdated;
		}
		DialogUpdated();
	}

	private void AttachScroll()
	{
		if (_scroll != null)
		{
			_scroll.ScrollChanged -= ContentSizeChanged;
		}
		_scroll = _list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
		if (_scroll != null)
		{
			_scroll.ScrollChanged += ContentSizeChanged;
		}
	}

	private void ContentSizeChanged(object? sender, ScrollChangedEventArgs e)
	{
		if (e.OffsetDelta.Y < 0 && _scroll != null
			&& _scroll.Extent.Height - _scroll.Viewport.Height - _scroll.Offset.Y > 1)
		{
			_followingTail = false;
		}
		else if (_followingTail && (e.ExtentDelta.Y != 0 || e.ViewportDelta.Y != 0))
		{
			ScrollToLatest();
		}
	}

	private void DialogUpdated()
	{
		_followingTail = true;
		ScrollToLatest();
	}

	private void ScrollToLatest()
	{
		if (!_followingTail || _scrollQueued || _closed || !IsVisible || _model?.Entries.Count is not > 0)
		{
			return;
		}
		_scrollQueued = true;
		Dispatcher.UIThread.Post(() =>
		{
			_scrollQueued = false;
			if (_followingTail && !_closed && IsVisible && _model?.Entries.Count > 0)
			{
				_list.ScrollIntoView(_model.Entries[^1]);
				_scroll?.ScrollToEnd();
			}
		}, DispatcherPriority.Background);
	}
}
