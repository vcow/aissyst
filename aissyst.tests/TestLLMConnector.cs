using aissyst.core.LLMConnector;

namespace aissyst.tests;

internal sealed class TestLLMConnector : ILLMConnector
{
	public Task InitializeAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		return Task.CompletedTask;
	}
}
