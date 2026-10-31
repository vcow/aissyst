namespace aissyst.core.Processor;

// Serializes processing and complete history replacement, including their presentation notifications.
public sealed class DialogCoordinator(ITextProcessor processor)
{
	private readonly SemaphoreSlim _gate = new(1, 1);
	public event Action<DialogMessage>? MessageCompleted;
	public event Action<IReadOnlyList<DialogMessage>>? HistoryReplaced;
	public event Action<IReadOnlyList<DialogMessage>>? HistoryRequested;
	public event Action<IReadOnlyList<DialogMessage>>? DialogUpdated;

	public async Task InitializeAsync(CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken);
		try
		{
			await processor.InitializeAsync(cancellationToken);
			NotifyDialogUpdated();
		}
		finally
		{
			_gate.Release();
		}
	}

	public IReadOnlyList<DialogMessage> GetDialog(DateTime fromTime) => processor.GetDialog(fromTime);

	public async Task ProcessAsync(DialogMessage message, CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken);
		try
		{
			var completed = await processor.ProcessAsync(message, cancellationToken);
			if (completed != null)
			{
				MessageCompleted?.Invoke(completed);
			}
			NotifyDialogUpdated();
		}
		finally
		{
			_gate.Release();
		}
	}

	public async Task ReloadAsync(CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken);
		try
		{
			if (processor is IReloadableTextProcessor reloadable)
			{
				await reloadable.ReloadAsync(cancellationToken);
			}
			cancellationToken.ThrowIfCancellationRequested();
			HistoryReplaced?.Invoke(processor.GetDialog(DateTime.MinValue));
			NotifyDialogUpdated();
		}
		finally
		{
			_gate.Release();
		}
	}

	public async Task ShowHistoryAsync(CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken);
		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			HistoryRequested?.Invoke(processor.GetDialog(DateTime.MinValue));
		}
		finally
		{
			_gate.Release();
		}
	}

	private void NotifyDialogUpdated() => DialogUpdated?.Invoke(processor.GetDialog(DateTime.MinValue));
}
