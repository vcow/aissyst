namespace aissyst.core.Processor;

// Serializes processing and complete history replacement, including their presentation notifications.
public sealed class DialogCoordinator(ITextProcessor processor)
{
	private readonly SemaphoreSlim _gate = new(1, 1);
	public event Action<DialogMessage>? MessageCompleted;
	public event Action<IReadOnlyList<DialogMessage>>? HistoryReplaced;

	public Task InitializeAsync(CancellationToken cancellationToken = default) => processor.InitializeAsync(cancellationToken);
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
		}
		finally
		{
			_gate.Release();
		}
	}
}
