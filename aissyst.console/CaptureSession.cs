using System.Diagnostics;
using System.Threading.Channels;
using aissyst.core;
using aissyst.core.Audio;
using Microsoft.Extensions.Hosting;

namespace aissyst.console;

public sealed class CaptureSession : IAsyncDisposable
{
	private readonly CancellationTokenSource _cancellation;
	private readonly IAudioCapture _audioCapture;
	private readonly IApplicationSettings _settings;
	private readonly ConsoleCaptureSession _records;
	private readonly Stopwatch _clock = new();

	private readonly Channel<SessionEvent> _events = Channel.CreateBounded<SessionEvent>(
		new BoundedChannelOptions(256) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

	private bool _started;
	private Task? _disposeTask;

	public CaptureSession(CaptureSessionContext context, IHostApplicationLifetime applicationLifetime,
		IAudioCapture audioCapture, IApplicationSettings settings, ConsoleCaptureSession records)
	{
		Context = context;
		_audioCapture = audioCapture;
		_settings = settings;
		_records = records;
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

	public async Task CompleteRecordingsAsync(CancellationToken cancellationToken = default)
	{
		if (!_started || Completion.IsCompleted)
		{
			await Completion;
			return;
		}

		var acknowledgement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		try
		{
			await _events.Writer.WriteAsync(new SessionEvent(EventKind.Complete, Acknowledgement: acknowledgement), cancellationToken);
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
				await _events.Writer.WriteAsync(new SessionEvent(EventKind.Frame, sourceType, frame, At: _clock.Elapsed), cancellationToken);
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
				await _events.Writer.WriteAsync(new SessionEvent(EventKind.Failure, sourceType, Error: exception), cancellationToken);
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
				_events.Writer.TryWrite(new SessionEvent(EventKind.Tick, At: _clock.Elapsed));
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
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
					item.Acknowledgement!.TrySetResult();
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

	private sealed record SessionEvent(
		EventKind Kind,
		AudioSourceType Source = AudioSourceType.Microphone,
		AudioFrame? Frame = null,
		Exception? Error = null,
		TaskCompletionSource? Acknowledgement = null,
		TimeSpan At = default);
}
