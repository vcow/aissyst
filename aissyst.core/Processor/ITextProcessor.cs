namespace aissyst.core.Processor;

public interface ITextProcessor
{
	bool IsReady { get; }
	Task InitializeAsync(CancellationToken cancellationToken = default);
	DialogMessage? Process(DialogMessage message);
	IReadOnlyList<DialogMessage> GetDialog(DateTime fromTime);
}
