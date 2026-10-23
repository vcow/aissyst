namespace aissyst.core.Processor;

public interface ISessionTextProcessor : ITextProcessor
{
	Task BeginSessionAsync(CancellationToken cancellationToken = default);
	Task CompleteSessionAsync(CancellationToken cancellationToken = default);
}
