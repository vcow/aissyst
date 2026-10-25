using System.Collections.Immutable;
using Microsoft.Extensions.Logging;

namespace aissyst.core.Processor;

public sealed class LoggingTextProcessor : ITextProcessor
{
	private readonly ILogger _logger;
	private readonly DialogMessageComparer _comparer = new();
	private readonly Lock _sync = new();
	private DialogMessage? _pendingMessage;

	private ImmutableSortedSet<DialogMessage> _dialog;

	public LoggingTextProcessor(ILogger<LoggingTextProcessor> logger)
	{
		_logger = logger;
		_dialog = ImmutableSortedSet.Create<DialogMessage>(_comparer);
	}

	public bool IsReady { get; private set; }

	public Task InitializeAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		IsReady = true;
		return Task.CompletedTask;
	}

	public DialogMessage? Process(DialogMessage message)
	{
		if (!IsReady)
		{
			throw new InvalidOperationException("The text processor has not been initialized.");
		}

		if (string.IsNullOrWhiteSpace(message.Text))
		{
			return null;
		}
		lock (_sync)
		{
			DialogMessage? completed = null;
			var normalized = message with { Text = message.Text.Trim() };
			if (_pendingMessage is { } pending && pending.Source == message.Source)
			{
				_pendingMessage = normalized with { Text = pending.Text + " " + normalized.Text };
			}
			else
			{
				completed = _pendingMessage;
				_pendingMessage = normalized;
			}
			_dialog = _dialog.Add(normalized);
			if (completed != null)
			{
				_logger.LogDebug("Completed a dialog message from {Source}.", completed.Source);
			}
			return completed;
		}
	}

	public IReadOnlyList<DialogMessage> GetDialog(DateTime fromTime)
	{
		return Volatile.Read(ref _dialog).ToDialog(fromTime);
	}
}
