using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace aissyst.core.Transcription;

public sealed class TranscriptionQueue
{
	private readonly object _sync = new();
	private readonly Channel<TranscriptionRequest> _requests;

	public TranscriptionQueue(IApplicationSettings settings, ILogger<TranscriptionQueue> logger)
	{
		_requests = Channel.CreateBounded<TranscriptionRequest>(new BoundedChannelOptions(settings.TranscriptionQueueCapacity)
		{
			FullMode = BoundedChannelFullMode.DropOldest,
			AllowSynchronousContinuations = false
		}, dropped => logger.LogWarning(
			"Transcription queue is full. Removed 1 oldest pending recording: {RecordingId} ({Source}, session {SessionId}).",
			dropped.Id, dropped.Source, dropped.SessionId));
	}

	public int Count
	{
		get
		{
			lock (_sync)
			{
				return _requests.Reader.Count;
			}
		}
	}

	public void Enqueue(TranscriptionRequest request)
	{
		ArgumentNullException.ThrowIfNull(request);
		lock (_sync)
		{
			_requests.Writer.TryWrite(request);
		}
	}

	public async ValueTask<TranscriptionRequest> DequeueAsync(CancellationToken cancellationToken = default)
	{
		while (true)
		{
			cancellationToken.ThrowIfCancellationRequested();
			lock (_sync)
			{
				if (_requests.Reader.TryRead(out var request))
				{
					return request;
				}
			}
			await _requests.Reader.WaitToReadAsync(cancellationToken);
		}
	}

	// Enqueue and dequeue share this lock so clearing removes exactly the currently pending records.
	public int Clear()
	{
		lock (_sync)
		{
			var removed = 0;
			while (_requests.Reader.TryRead(out _))
			{
				removed++;
			}
			return removed;
		}
	}
}
