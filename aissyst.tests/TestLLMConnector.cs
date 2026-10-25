using aissyst.core.LLMConnector;
using aissyst.core.LLMConnector.DTO;

namespace aissyst.tests;

internal sealed class TestLLMConnector : ILLMConnector
{
	internal int SessionInitializations;
	internal int Requests;
	internal Func<Session, CancellationToken, Task<DialogResponse>>? Initialize;
	internal Func<DialogRequest, CancellationToken, Task<DialogResponse>>? Send;
	public Task InitializeAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		return Task.CompletedTask;
	}

	public Task<DialogResponse> InitializeSession(Session session, CancellationToken cancellationToken = default)
	{
		Interlocked.Increment(ref SessionInitializations);
		return Initialize?.Invoke(session, cancellationToken)
			?? throw new InvalidOperationException("Unexpected LLM session initialization.");
	}

	public Task<DialogResponse> SendRequest(DialogRequest request, CancellationToken cancellationToken = default)
	{
		Interlocked.Increment(ref Requests);
		return Send?.Invoke(request, cancellationToken)
			?? throw new InvalidOperationException("Unexpected LLM request.");
	}
}
