namespace aissyst.core.LLMConnector;

public sealed record LLMModel(string Id, string Name);

public interface IModelSelector
{
	Task InitializeAsync(IReadOnlyList<LLMModel> models, CancellationToken cancellationToken = default);
	LLMModel GetSelectedModel();
}
