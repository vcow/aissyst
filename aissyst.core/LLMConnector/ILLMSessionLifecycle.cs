using aissyst.core.LLMConnector.DTO;

namespace aissyst.core.LLMConnector;

// The application owns session data; the connector owns the connection and response chain.
public interface ILLMSessionLifecycle
{
	void ConfigureSession(string instructions, Func<Session> getSession,
		Action<Session, DialogResponse> acceptRecovery, Func<DialogResponse, bool> acceptResponse);
}
