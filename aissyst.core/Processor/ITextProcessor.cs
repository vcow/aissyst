namespace aissyst.core.Processor;

public interface ITextProcessor
{
	bool IsReady { get; }
	Task InitializeAsync(CancellationToken cancellationToken = default);
	Task ProcessAsync(DateTime timestamp, AudioSourceType source, string text, CancellationToken cancellationToken = default);
}
