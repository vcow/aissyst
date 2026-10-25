namespace aissyst.core.LLMConnector.Model;

public interface ISessionSelector
{
	Task InitializeAsync(CancellationToken cancellationToken = default);
	int GetSelectedSessionId();
}
