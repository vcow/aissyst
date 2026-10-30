namespace aissyst.core.LLMConnector;

public sealed record LLMReasoningLevel(string Effort, string Description);

public sealed record LLMModel(string Id, string Name)
{
	public IReadOnlyList<LLMReasoningLevel> ReasoningLevels { get; init; } = [];
	public string? DefaultReasoningEffort { get; init; }
}

public interface IModelSelector
{
	Task InitializeAsync(IReadOnlyList<LLMModel> models, CancellationToken cancellationToken = default);
	LLMModel GetSelectedModel();
	string? GetSelectedReasoningEffort();
}
