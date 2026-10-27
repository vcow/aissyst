using System.Collections.Concurrent;
using aissyst.core.Database;
using aissyst.core.LLMConnector.Model;
using aissyst.core.Processor;

namespace aissyst.tests;

internal sealed class TestSessionSelector : ISessionSelector
{
	internal int Calls;
	internal int SessionId = 7;
	private int? _selectedId;
	public Task InitializeAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (!_selectedId.HasValue)
		{
			Interlocked.Increment(ref Calls);
			_selectedId = SessionId;
		}
		return Task.CompletedTask;
	}

	public int GetSelectedSessionId() => _selectedId
		?? throw new InvalidOperationException("The session selector has not been initialized.");
}

internal sealed class TestSessionDatabase : IDatabase
{
	internal readonly ConcurrentQueue<int> RequestedSessions = new();
	internal SessionContext Session = new(new SessionInfo(7, DateTime.UtcNow),
		"Test preview", [new ContextRecord("Background", "Test context")], []);
	internal Func<int, CancellationToken, Task<SessionContext>>? Load;

	public Task<SessionContext> GetSessionAsync(int sessionId, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		RequestedSessions.Enqueue(sessionId);
		return Load?.Invoke(sessionId, cancellationToken) ?? Task.FromResult(Session);
	}

	public Task<IReadOnlyList<ContextInfo>> GetContextsAsync(CancellationToken cancellationToken = default) =>
		throw new InvalidOperationException("Unexpected context enumeration.");
	public Task<IReadOnlyList<SessionInfo>> GetSessionsAsync(CancellationToken cancellationToken = default) =>
		throw new InvalidOperationException("Unexpected session enumeration.");
	public Task<SessionContext> CreateSessionAsync(int contextId, CancellationToken cancellationToken = default) =>
		throw new InvalidOperationException("Unexpected session creation.");
	public Task SetSessionDialogAsync(int sessionId, IReadOnlyList<DialogMessage> dialog, CancellationToken cancellationToken = default) =>
		throw new InvalidOperationException("Unexpected dialog save.");
}
