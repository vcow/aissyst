using aissyst.core;
using aissyst.core.Audio;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace aissyst.console;

public sealed class CaptureSession(CaptureSessionContext context, IHostApplicationLifetime applicationLifetime,
	IAudioCapture audioCapture, IApplicationSettings settings, ConsoleCaptureSession records, ILogger<CaptureSession> logger)
	: IAsyncDisposable
{
	private AudioCaptureSession? _session;
	private Task? _disposal;
	public CaptureSessionContext Context { get; } = context;
	public Task Completion => _session?.Completion ?? Task.CompletedTask;

	public void Start()
	{
		ObjectDisposedException.ThrowIf(_disposal != null, this);
		_session ??= new AudioCaptureSession(audioCapture, settings, Context.Microphone, Context.Application,
			records.Publish, logger, applicationLifetime.ApplicationStopping);
		_session.Start();
	}

	public Task CompleteRecordingsAsync(CancellationToken cancellationToken = default) =>
		_session?.CompleteRecordingsAsync(cancellationToken) ?? Task.CompletedTask;

	public Task<IDisposable> PauseAndCompleteRecordingsAsync(CancellationToken cancellationToken = default) =>
		_session?.PauseAndCompleteRecordingsAsync(cancellationToken) ?? Task.FromResult<IDisposable>(new EmptyPause());

	public ValueTask DisposeAsync() => new(_disposal ??= _session?.DisposeAsync().AsTask() ?? Task.CompletedTask);

	private sealed class EmptyPause : IDisposable
	{
		public void Dispose() { }
	}
}
