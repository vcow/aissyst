using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text;
using Avalonia.Threading;
using aissyst.desktop.Logging;
using Microsoft.Extensions.Logging;

namespace aissyst.desktop.ViewModels;

internal sealed class LogViewModel : ViewModel
{
	private readonly Lock _sync = new();
	private readonly List<LogEntry> _entries = [];
	private readonly List<LogEntry> _pending = [];
	private readonly LogCollection _visible = new();
	private bool _updatePending;
	private bool _showInfo = true;
	private bool _showWarning = true;
	private bool _showError = true;

	public LogViewModel() => Entries = new ReadOnlyObservableCollection<LogEntry>(_visible);

	public ReadOnlyObservableCollection<LogEntry> Entries { get; }
	public bool ShowInfo
	{
		get => _showInfo;
		set => SetFilter(ref _showInfo, value, nameof(ShowInfo));
	}
	public bool ShowWarning
	{
		get => _showWarning;
		set => SetFilter(ref _showWarning, value, nameof(ShowWarning));
	}
	public bool ShowError
	{
		get => _showError;
		set => SetFilter(ref _showError, value, nameof(ShowError));
	}

	public void Append(LogEntry entry)
	{
		lock (_sync)
		{
			_entries.Add(entry);
			_pending.Add(entry);
			if (_updatePending)
			{
				return;
			}
			_updatePending = true;
		}
		Dispatcher.UIThread.Post(FlushPending, DispatcherPriority.Background);
	}

	public void Clear()
	{
		Dispatcher.UIThread.VerifyAccess();
		lock (_sync)
		{
			_entries.Clear();
			_pending.Clear();
		}
		_visible.Clear();
	}

	public LogEntry[] GetSnapshot()
	{
		lock (_sync)
		{
			return _entries.ToArray();
		}
	}

	public static string Format(IEnumerable<LogEntry> entries)
	{
		var text = new StringBuilder();
		foreach (var entry in entries)
		{
			text.AppendLine(entry.Text);
		}
		return text.ToString();
	}

	private void FlushPending()
	{
		LogEntry[] entries;
		lock (_sync)
		{
			entries = _pending.ToArray();
			_pending.Clear();
			_updatePending = false;
		}
		_visible.AddRange(entries.Where(IsVisible).ToArray());
	}

	private bool IsVisible(LogEntry entry) => entry.Level switch
	{
		LogLevel.None => false,
		LogLevel.Warning => ShowWarning,
		LogLevel.Error or LogLevel.Critical => ShowError,
		_ => ShowInfo
	};

	private void SetFilter(ref bool field, bool value, string property)
	{
		Dispatcher.UIThread.VerifyAccess();
		if (field == value)
		{
			return;
		}
		field = value;
		LogEntry[] entries;
		lock (_sync)
		{
			entries = _entries.ToArray();
			_pending.Clear();
		}
		_visible.Replace(entries.Where(IsVisible).ToArray());
		Notify(property);
	}

	private sealed class LogCollection : ObservableCollection<LogEntry>
	{
		public void AddRange(LogEntry[] entries)
		{
			if (entries.Length == 0)
			{
				return;
			}
			CheckReentrancy();
			var index = Count;
			foreach (var entry in entries)
			{
				Items.Add(entry);
			}
			Changed();
			OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, entries, index));
		}

		public void Replace(LogEntry[] entries)
		{
			CheckReentrancy();
			Items.Clear();
			foreach (var entry in entries)
			{
				Items.Add(entry);
			}
			Changed();
			OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
		}

		private void Changed()
		{
			OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
			OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
		}
	}
}
