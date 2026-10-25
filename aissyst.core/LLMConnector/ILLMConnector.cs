using aissyst.core.LLMConnector.DTO;

namespace aissyst.core.LLMConnector;

public interface ILLMConnector
{
	Task InitializeAsync(CancellationToken cancellationToken = default);
	Task<DialogResponse> InitializeSession(Session session, CancellationToken cancellationToken = default);
	Task<DialogResponse> SendRequest(DialogRequest request, CancellationToken cancellationToken = default);
}
