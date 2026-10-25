using aissyst.core.LLMConnector.DTO;
using aissyst.core.Processor;

namespace aissyst.core.Database;

public interface IDatabase
{
	Task<IReadOnlyList<ContextInfo>> GetContextsAsync(CancellationToken cancellationToken = default);
	Task<IReadOnlyList<SessionInfo>> GetSessionsAsync(CancellationToken cancellationToken = default);
	Task<SessionContext> CreateSessionAsync(int contextId, CancellationToken cancellationToken = default);
	Task<SessionContext> GetSessionAsync(int sessionId, CancellationToken cancellationToken = default);
	Task SetSessionDialogAsync(int sessionId, IReadOnlyList<DialogMessage> dialog, CancellationToken cancellationToken = default);
}