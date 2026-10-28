namespace aissyst.core.Processor;

public interface IReloadableTextProcessor : ITextProcessor
{
	Task ReloadAsync(CancellationToken cancellationToken = default);
}
