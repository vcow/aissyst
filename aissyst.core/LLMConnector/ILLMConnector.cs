namespace aissyst.core.LLMConnector;

public interface ILLMConnector
{
	Task InitializeAsync(CancellationToken cancellationToken = default);
}