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
	private readonly Lock _sync = new();
	private CancellationTokenSource? _active;
	private TaskCompletionSource? _completion;
	public bool IsBusy { get { lock (_sync) { return _active != null; } } }
	public event EventHandler? ConnectionLost;
	internal void Disconnect()
	{
		lock (_sync)
		{
			_active?.Cancel();
		}
		ConnectionLost?.Invoke(this, EventArgs.Empty);
	}
	public Task CancelAsync(CancellationToken cancellationToken = default)
	{
		if (IsBusy)
		{
			Disconnect();
		}
		return Task.CompletedTask;
	}

	public Task InitializeAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		return Task.CompletedTask;
	}

	public async Task ReconnectAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		Task? active;
		lock (_sync)
		{
			active = _completion?.Task;
		}
		if (active != null)
		{
			await active.WaitAsync(cancellationToken);
		}
		Reconnects++;
		Connection = new object();
	}

	public Task<LLMResponse> InitializeSession(string instructions, Session session, CancellationToken cancellationToken = default) => RunAsync(async token =>
	{
		Interlocked.Increment(ref SessionInitializations);
		Instructions = instructions;
		var connection = Connection;
		var response = await (Initialize?.Invoke(session, token)
			?? throw new InvalidOperationException("Unexpected LLM session initialization."));
		// Existing fixtures construct payloads without specifying the initialization request ID.
		response.RequestId = session.RequestId;
		return new LLMResponse(response, new TestContinuation(connection, "session"));
	}, cancellationToken);

	public Task<LLMResponse> SendRequest(DialogRequest request, ILLMContinuation continuation, CancellationToken cancellationToken = default) => RunAsync(async token =>
	{
		Interlocked.Increment(ref Requests);
		lock (Parents)
		{
			Parents.Add(continuation);
		}
		var connection = Connection;
		var response = await (Send?.Invoke(request, token)
			?? throw new InvalidOperationException("Unexpected LLM request."));
		return new LLMResponse(response, new TestContinuation(connection, request.RequestId.ToString()));
	}, cancellationToken);

	private async Task<LLMResponse> RunAsync(Func<CancellationToken, Task<LLMResponse>> action, CancellationToken cancellationToken)
	{
		CancellationTokenSource active;
		TaskCompletionSource completion;
		lock (_sync)
		{
			if (_active != null)
			{
				throw new InvalidOperationException("The LLM connector is busy.");
			}
			active = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			_active = active;
			completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			_completion = completion;
		}
		try
		{
			return await action(active.Token).WaitAsync(active.Token);
		}
		finally
		{
			lock (_sync)
			{
				_active = null;
				active.Dispose();
				completion.TrySetResult();
			}
		}
	}
}
