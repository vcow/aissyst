using aissyst.core.Processor;

namespace aissyst.core.Database;

public interface IDatabase
{
	Task<IReadOnlyList<ContextInfo>> GetContextsAsync(CancellationToken cancellationToken = default);
	Task<IReadOnlyList<SessionInfo>> GetSessionsAsync(CancellationToken cancellationToken = default);
	Task<ContextInfo> CreateContextAsync(string contextTemplatePath, string? contextName = null, CancellationToken cancellationToken = default);
	Task<SessionInfo> CreateSessionAsync(int contextId, CancellationToken cancellationToken = default);
	Task DeleteContextAsync(int contextId, CancellationToken cancellationToken = default);
	Task DeleteSessionAsync(int sessionId, CancellationToken cancellationToken = default);
	Task<SessionContext> GetSessionAsync(int sessionId, CancellationToken cancellationToken = default);
	Task SetSessionDialogAsync(int sessionId, IReadOnlyList<DialogMessage> dialog, CancellationToken cancellationToken = default);
	Task AddSessionDialogRecordAsync(int sessionId, DialogMessage message, CancellationToken cancellationToken = default);
	Task ExportSessionAsync(int sessionId, string filePath, CancellationToken cancellationToken = default);
	Task<SessionInfo> ImportSessionAsync(string filePath, CancellationToken cancellationToken = default);
	Task<string> GetSessionInitialInstructionsAsync(int sessionId, CancellationToken cancellationToken = default);
}