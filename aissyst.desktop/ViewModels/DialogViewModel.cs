using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using aissyst.core;
using aissyst.core.Processor;

namespace aissyst.desktop.ViewModels;

internal sealed class DialogEntryViewModel(DialogMessage message) : ViewModel
{
	public DialogMessage Message { get; private set; } = message;
	public string Text => Message.Text;
	public string Header => $"{Role(Message.Source)} · {Message.Timestamp.ToLocalTime():HH:mm:ss}";
	public IBrush Foreground => Message.Source == AudioSourceType.Microphone ? Brushes.LightGray : Brushes.White;
	public Thickness Spacing => new(Message.Source == AudioSourceType.Microphone ? 28 : 10, 12, 10, 12);

	public void Update(DialogMessage value)
	{
		if (Message == value)
		{
			return;
		}
		Message = value;
		Notify(nameof(Text));
		Notify(nameof(Header));
	}

	public static string Role(AudioSourceType source) => source switch
	{
		AudioSourceType.Application => "Interviewer",
		AudioSourceType.Microphone => "Interviewee",
		_ => "Unknown speaker"
	};
}

internal sealed class DialogViewModel
{
	private readonly Lock _sync = new();
	private readonly ObservableCollection<DialogEntryViewModel> _entries = [];
	private DialogMessage[] _snapshot = [];
	private bool _updatePending;
	public ReadOnlyObservableCollection<DialogEntryViewModel> Entries { get; }
	public event Action? Updated;

	public DialogViewModel() => Entries = new(_entries);

	public void ReplaceHistory(IReadOnlyList<DialogMessage> messages)
	{
		var snapshot = messages.Where(message => !string.IsNullOrWhiteSpace(message.Text)).ToArray();
		lock (_sync)
		{
			if (_snapshot.SequenceEqual(snapshot))
			{
				return;
			}
			_snapshot = snapshot;
			if (_updatePending)
			{
				return;
			}
			_updatePending = true;
		}
		Dispatcher.UIThread.Post(UpdateEntries, DispatcherPriority.Background);
	}

	public DialogMessage[] GetSnapshot()
	{
		lock (_sync)
		{
			return _snapshot.ToArray();
		}
	}

	private void UpdateEntries()
	{
		DialogMessage[] snapshot;
		lock (_sync)
		{
			snapshot = _snapshot;
			_updatePending = false;
		}
		while (_entries.Count > snapshot.Length)
		{
			_entries.RemoveAt(_entries.Count - 1);
		}
		for (var index = 0; index < snapshot.Length; index++)
		{
			if (index == _entries.Count)
			{
				_entries.Add(new(snapshot[index]));
			}
			else if (_entries[index].Message.Source == snapshot[index].Source)
			{
				_entries[index].Update(snapshot[index]);
			}
			else
			{
				_entries[index] = new(snapshot[index]);
			}
		}
		Updated?.Invoke();
	}

	public static string Format(IEnumerable<DialogMessage> snapshot)
	{
		var text = new StringBuilder();
		foreach (var message in snapshot)
		{
			text.Append('[').Append(message.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
				.Append("] ").Append(DialogEntryViewModel.Role(message.Source)).AppendLine(":");
			text.AppendLine(message.Text).AppendLine();
		}
		return text.ToString();
	}
}
