namespace aissyst.core.Processor;

public interface ITextProcessor
{
	bool IsReady { get; }
	Task InitializeAsync(CancellationToken cancellationToken = default);
	DialogMessage? Process(DialogMessage message);
	Task<DialogMessage?> ProcessAsync(DialogMessage message, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		return Task.FromResult(Process(message));
	}
	IReadOnlyList<DialogMessage> GetDialog(DateTime fromTime);
}
