using Microsoft.Extensions.Hosting;

namespace aissyst.console;

public sealed class CaptureSession : IAsyncDisposable
{
	private readonly CancellationTokenSource _cancellation;
	private bool _started;
	private Task? _disposeTask;

	public CaptureSession(CaptureSessionContext context, IHostApplicationLifetime applicationLifetime)
	{
		Context = context;
		_cancellation = CancellationTokenSource.CreateLinkedTokenSource(applicationLifetime.ApplicationStopping);
	}

	public CaptureSessionContext Context { get; }
	public Task Completion { get; private set; } = Task.CompletedTask;

	public void Start()
	{
		ObjectDisposedException.ThrowIf(_disposeTask != null, this);
		if (_started)
			throw new InvalidOperationException("A session can only be started once. Create a new scope to start another session.");

		// Require both sources to be selected before starting any work.
		_ = Context.Microphone;
		_ = Context.Application;
		_started = true;
		Completion = RunAsync(_cancellation.Token);
	}

	public ValueTask DisposeAsync()
	{
		return new ValueTask(_disposeTask ??= StopAndDisposeAsync());
	}

	private static async Task RunAsync(CancellationToken cancellationToken)
	{
		try
		{
			// Placeholder: future capture work will use the scoped session context.
			await Task.Delay(Timeout.Infinite, cancellationToken);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			// Disposing the scope or stopping the host ends the session normally.
		}
	}

	private async Task StopAndDisposeAsync()
	{
		try
		{
			await _cancellation.CancelAsync();
		}
		finally
		{
			try
			{
				await Completion;
			}
			finally
			{
				_cancellation.Dispose();
			}
		}
	}
}
