using aissyst.core.LLMConnector;
using aissyst.core.LLMConnector.DTO;

namespace aissyst.tests;

internal sealed class TestLLMConnector : ILLMConnector
{
	internal int SessionInitializations;
	internal int Requests;
	public Task InitializeAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		return Task.CompletedTask;
	}

	public Task<DialogResponse> InitializeSession(Session session)
	{
		Interlocked.Increment(ref SessionInitializations);
		throw new InvalidOperationException("Unexpected LLM session initialization.");
	}

	public Task<DialogResponse> SendRequest(DialogRequest request)
	{
		Interlocked.Increment(ref Requests);
		throw new InvalidOperationException("Unexpected LLM request.");
	}
}
