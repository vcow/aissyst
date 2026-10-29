using aissyst.core.LLMConnector;
using aissyst.core.LLMConnector.DTO;

namespace aissyst.tests;

internal sealed class TestContinuation(object connectionId, string id = "test") : ILLMContinuation
{
	public object ConnectionId { get; } = connectionId;
	internal string Id { get; } = id;
	internal bool Disposed;
	public bool IsValid => !Disposed;
	public ILLMContinuation Retain() => new TestContinuation(ConnectionId, Id);
	public void Dispose() => Disposed = true;
}

internal sealed class TestLLMConnector : ILLMConnector
{
	internal int SessionInitializations;
	internal int Requests;
	internal int Reconnects;
	internal string? Instructions;
	internal object Connection = new();
	internal Func<Session, CancellationToken, Task<DialogResponse>>? Initialize;
	internal Func<DialogRequest, CancellationToken, Task<DialogResponse>>? Send;
	internal readonly List<ILLMContinuation> Parents = [];
	public event EventHandler? ConnectionLost;
	internal void Disconnect() => ConnectionLost?.Invoke(this, EventArgs.Empty);

	public Task InitializeAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		return Task.CompletedTask;
	}

	public Task ReconnectAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		Reconnects++;
		Connection = new object();
		return Task.CompletedTask;
	}

	public async Task<LLMResponse> InitializeSession(string instructions, Session session, CancellationToken cancellationToken = default)
	{
		Interlocked.Increment(ref SessionInitializations);
		Instructions = instructions;
		var connection = Connection;
		var response = await (Initialize?.Invoke(session, cancellationToken)
			?? throw new InvalidOperationException("Unexpected LLM session initialization."));
		// Existing fixtures construct payloads without specifying the initialization request ID.
		response.RequestId = session.RequestId;
		return new LLMResponse(response, new TestContinuation(connection, "session"));
	}

	public async Task<LLMResponse> SendRequest(DialogRequest request, ILLMContinuation continuation, CancellationToken cancellationToken = default)
	{
		Interlocked.Increment(ref Requests);
		lock (Parents)
		{
			Parents.Add(continuation);
		}
		var connection = Connection;
		var response = await (Send?.Invoke(request, cancellationToken)
			?? throw new InvalidOperationException("Unexpected LLM request."));
		return new LLMResponse(response, new TestContinuation(connection, request.RequestId.ToString()));
	}
}
