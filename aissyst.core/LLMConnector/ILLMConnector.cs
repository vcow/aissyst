using aissyst.core.LLMConnector.DTO;

namespace aissyst.core.LLMConnector;

// A leased, provider-specific continuation. Application DTOs do not contain transport metadata.
public interface ILLMContinuation : IDisposable
{
	object ConnectionId { get; }
	bool IsValid { get; }
	ILLMContinuation Retain();
}

public sealed record LLMResponse(DialogResponse Payload, ILLMContinuation Continuation);

public interface ILLMConnector
{
	event EventHandler? ConnectionLost;
	Task InitializeAsync(CancellationToken cancellationToken = default);
	Task ReconnectAsync(CancellationToken cancellationToken = default);
	Task<LLMResponse> InitializeSession(string instructions, Session session, CancellationToken cancellationToken = default);
	Task<LLMResponse> SendRequest(DialogRequest request, ILLMContinuation continuation, CancellationToken cancellationToken = default);
}
