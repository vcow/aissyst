namespace aissyst.core.Processor;

public interface ITextProcessor
{
	bool IsReady { get; }
	Task InitializeAsync(CancellationToken cancellationToken = default);
}