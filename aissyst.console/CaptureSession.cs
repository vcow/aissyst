using System.Diagnostics;
using System.Threading.Channels;
using aissyst.core;
using aissyst.core.Audio;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace aissyst.console;

public sealed class CaptureSession : IAsyncDisposable
{
	private readonly CancellationTokenSource _cancellation;
	private readonly IAudioCapture _audioCapture;
	private readonly IApplicationSettings _settings;
	private readonly ConsoleCaptureSession _records;
	private readonly ILogger<CaptureSession> _logger;
	private readonly Stopwatch _clock = new();
	private const int EventCapacity = 512;

	private readonly Channel<SessionEvent> _events = Channel.CreateBounded<SessionEvent>(
		new BoundedChannelOptions(EventCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

	private bool _started;
	private int _processingPaused;
	private int _channelFullLogged;
	private Task? _disposeTask;

	public CaptureSession(CaptureSessionContext context, IHostApplicationLifetime applicationLifetime,
		IAudioCapture audioCapture, IApplicationSettings settings, ConsoleCaptureSession records, ILogger<CaptureSession> logger)
	{
		Context = context;
		_audioCapture = audioCapture;
		_settings = settings;
		_records = records;
		_logger = logger;
		_cancellation = CancellationTokenSource.CreateLinkedTokenSource(applicationLifetime.ApplicationStopping);
	}

	public CaptureSessionContext Context { get; }
	public Task Completion { get; private set; } = Task.CompletedTask;

	public void Start()
	{
		ObjectDisposedException.ThrowIf(_disposeTask != null, this);
		if (_started)
		{
			throw new InvalidOperationException("A session can only be started once. Create a new scope to start another session.");
		}

		var sources = _settings.AvailableSources;
		if (sources == AudioSourceType.None || (sources & ~AudioSourceType.Both) != AudioSourceType.None)
		{
			throw new InvalidOperationException("At least one supported audio source must be enabled.");
		}
		var selectedSources = (Context.Microphone != null ? AudioSourceType.Microphone : AudioSourceType.None)
			| (Context.Application != null ? AudioSourceType.Application : AudioSourceType.None);
		if (selectedSources == AudioSourceType.None || (selectedSources & ~sources) != AudioSourceType.None)
		{
			throw new InvalidOperationException("Select at least one of the enabled audio sources.");
		}
		_started = true;
		_clock.Start();
		Completion = RunAsync(_cancellation.Token);
	}

	public Task CompleteRecordingsAsync(CancellationToken cancellationToken = default) =>
		CompleteRecordingsAsync(null, cancellationToken);

	public async Task<IDisposable> PauseAndCompleteRecordingsAsync(CancellationToken cancellationToken = default)
	{
		var pause = new ProcessingPause();
		try
		{
			await CompleteRecordingsAsync(pause, cancellationToken);
			return pause;
		}
		catch
		{
			pause.Dispose();
			throw;
		}
	}

	private async Task CompleteRecordingsAsync(ProcessingPause? pause, CancellationToken cancellationToken)
	{
		if (!_started || Completion.IsCompleted)
		{
			await Completion;
			return;
		}

		var acknowledgement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		try
		{
			await WriteEventAsync(new SessionEvent(EventKind.Complete, Acknowledgement: acknowledgement, Pause: pause), cancellationToken);
		}
		catch (ChannelClosedException)
		{
			await Completion;
			return;
		}

		var finished = await Task.WhenAny(acknowledgement.Task, Completion).WaitAsync(cancellationToken);
		await finished;
	}

	public ValueTask DisposeAsync()
	{
		return new ValueTask(_disposeTask ??= StopAndDisposeAsync());
	}

	private async Task RunAsync(CancellationToken cancellationToken)
	{
		using var microphone = new AudioRecorder(_settings);
		using var application = new AudioRecorder(_settings);
		using var producersCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		var token = producersCancellation.Token;
		var producers = new List<Task>();
		if (Context.Microphone is { } selectedMicrophone)
		{
			producers.Add(PumpAsync(ct => _audioCapture.CaptureMicrophoneAsync(selectedMicrophone.Id, ct), AudioSourceType.Microphone, token));
		}
		if (Context.Application is { } selectedApplication)
		{
			producers.Add(PumpAsync(ct => _audioCapture.CaptureApplicationAudioAsync(selectedApplication, ct), AudioSourceType.Application, token));
		}
		producers.Add(PumpTimerAsync(token));
		try
		{
			await foreach (var item in _events.Reader.ReadAllAsync(cancellationToken))
			{
				if (_events.Reader.Count < EventCapacity / 2)
				{
					Interlocked.Exchange(ref _channelFullLogged, 0);
				}
				await HandleAsync(item, microphone, application);
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			// Disposing the scope or stopping the host ends listening normally.
		}
		finally
		{
			await producersCancellation.CancelAsync();
			await Task.WhenAll(producers);
			try
			{
				while (_events.Reader.TryRead(out var item))
				{
					if (item.Kind != EventKind.Failure)
					{
						await HandleAsync(item, microphone, application);
					}
				}

				await CompleteAsync(microphone, application);
			}
			finally
			{
				_events.Writer.TryComplete();
			}
		}
	}

	private async Task PumpAsync(Func<CancellationToken, IAsyncEnumerable<AudioFrame>> source, AudioSourceType sourceType,
		CancellationToken cancellationToken)
	{
		try
		{
			await foreach (var frame in source(cancellationToken))
			{
				await WriteEventAsync(new SessionEvent(EventKind.Frame, sourceType, frame, At: _clock.Elapsed), cancellationToken);
			}

			if (!cancellationToken.IsCancellationRequested)
			{
				throw new InvalidOperationException("The audio source ended unexpectedly.");
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
		}
		catch (Exception exception)
		{
			try
			{
				await WriteEventAsync(new SessionEvent(EventKind.Failure, sourceType, Error: exception), cancellationToken);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
			}
		}
	}

	private async Task PumpTimerAsync(CancellationToken cancellationToken)
	{
		using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
		try
		{
			while (await timer.WaitForNextTickAsync(cancellationToken))
			{
				if (Volatile.Read(ref _processingPaused) == 0
					&& !_events.Writer.TryWrite(new SessionEvent(EventKind.Tick, At: _clock.Elapsed)))
				{
					LogChannelFull();
				}
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
		}
	}

	private async ValueTask WriteEventAsync(SessionEvent item, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (!_events.Writer.TryWrite(item))
		{
			LogChannelFull();
			await _events.Writer.WriteAsync(item, cancellationToken);
		}
	}

	private void LogChannelFull()
	{
		if (_events.Reader.Count >= EventCapacity && Interlocked.Exchange(ref _channelFullLogged, 1) == 0)
		{
			_logger.LogWarning("Capture event channel is full (capacity {Capacity}). Audio producers may wait; source buffers may overflow.", EventCapacity);
		}
	}

	private async Task HandleAsync(SessionEvent item, AudioRecorder microphone, AudioRecorder application)
	{
		switch (item.Kind)
		{
			case EventKind.Frame:
				var recorder = item.Source == AudioSourceType.Microphone ? microphone : application;
				foreach (var recording in recorder.Append(item.Frame!, item.At))
				{
					await PublishIfPresentAsync(recording, item.Source);
				}
				break;
			case EventKind.Tick:
				await PublishIfPresentAsync(microphone.CompleteIfSilent(item.At), AudioSourceType.Microphone);
				await PublishIfPresentAsync(application.CompleteIfSilent(item.At), AudioSourceType.Application);
				break;
			case EventKind.Complete:
				try
				{
					await CompleteAsync(microphone, application);
					if (item.Pause != null && !_cancellation.IsCancellationRequested)
					{
						Volatile.Write(ref _processingPaused, 1);
						try
						{
							item.Acknowledgement!.TrySetResult();
							await item.Pause.Resumed.Task.WaitAsync(_cancellation.Token);
						}
						finally
						{
							Volatile.Write(ref _processingPaused, 0);
						}
					}
					else
					{
						item.Acknowledgement!.TrySetResult();
					}
				}
				catch (Exception exception)
				{
					item.Acknowledgement!.TrySetException(exception);
					throw;
				}

				break;
			case EventKind.Failure:
				throw new InvalidOperationException($"{item.Source} capture failed: {item.Error!.Message}", item.Error);
		}
	}

	private async Task CompleteAsync(AudioRecorder microphone, AudioRecorder application)
	{
		await PublishIfPresentAsync(microphone.Complete(), AudioSourceType.Microphone);
		await PublishIfPresentAsync(application.Complete(), AudioSourceType.Application);
	}

	private Task PublishIfPresentAsync(AudioRecording? recording, AudioSourceType source)
	{
		if (recording == null)
		{
			return Task.CompletedTask;
		}
		_records.Publish(source, recording);
		return Task.CompletedTask;
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

	private enum EventKind
	{
		Frame,
		Tick,
		Complete,
		Failure
	}

	private sealed class ProcessingPause : IDisposable
	{
		internal readonly TaskCompletionSource Resumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public void Dispose() => Resumed.TrySetResult();
	}

	private sealed record SessionEvent(
		EventKind Kind,
		AudioSourceType Source = AudioSourceType.Microphone,
		AudioFrame? Frame = null,
		Exception? Error = null,
		TaskCompletionSource? Acknowledgement = null,
		TimeSpan At = default,
		ProcessingPause? Pause = null);
}
