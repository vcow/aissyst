using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using aissyst.core.Audio;
using aissyst.core.LLMConnector;
using aissyst.core.Processor;
using aissyst.core.Transcription;
using Microsoft.Extensions.Logging;
using R3;

namespace aissyst.core.Controller;

public class ApplicationController : IAsyncDisposable
{
	private readonly ITranscriber _transcriber;
	private readonly ITextProcessor _textProcessor;
	private readonly ICaptureSession _captureSession;
	private readonly ILogger<ApplicationController> _logger;
	private readonly ILLMConnector _llmConnector;
	private readonly IApplicationSettings _settings;

	private readonly CompositeDisposable _disposables = new();

	private readonly CompositeDisposable _observableHandlers;
	private readonly CancellationTokenSource _cancellationTokenSource;
	private readonly CancellationToken _stoppingToken;

	private readonly Lock _trackedTasksLock = new();
	private ImmutableHashSet<Task> _trackedTasks = new HashSet<Task>().ToImmutableHashSet();

	private readonly ConcurrentQueue<(DateTime ts, AudioSourceType source, IReadOnlyList<AudioFrame> frames)> _audioRecordsQueue = new();
	private readonly SortedSet<(DateTime ts, AudioSourceType source, string text)> _textRecordsQueue = new(new TextRecordComparer());

	private readonly SemaphoreSlim _audioRecordsSemaphore;
	private readonly SemaphoreSlim _textRecordsSemaphore;
	private readonly SemaphoreSlim _transcriberSemaphore;
	private readonly SemaphoreSlim _textProcessorSemaphore;

	private bool _isDisposed;
	private Task? _disposeTask;

	public ApplicationController(ITranscriber transcriber, ITextProcessor textProcessor, ICaptureSession captureSession,
		ILogger<ApplicationController> logger, ILLMConnector llmConnector, IApplicationSettings settings)
	{
		_transcriber = transcriber;
		_textProcessor = textProcessor;
		_captureSession = captureSession;
		_logger = logger;
		_llmConnector = llmConnector;
		_settings = settings;

		Debug.Assert(settings is { MaxTextProcessingCount: > 0, MaxTranscriptionProcessingCount: > 0 });

		_cancellationTokenSource = new CancellationTokenSource().AddTo(_disposables);
		_stoppingToken = _cancellationTokenSource.Token;

		_audioRecordsSemaphore = new SemaphoreSlim(1, 1).AddTo(_disposables);
		_textRecordsSemaphore = new SemaphoreSlim(1, 1).AddTo(_disposables);
		_transcriberSemaphore = new SemaphoreSlim(settings.MaxTranscriptionProcessingCount, settings.MaxTranscriptionProcessingCount).AddTo(_disposables);
		_textProcessorSemaphore = new SemaphoreSlim(settings.MaxTextProcessingCount, settings.MaxTextProcessingCount).AddTo(_disposables);

		_observableHandlers = new CompositeDisposable().AddTo(_disposables);

		_captureSession.MicrophoneRecordObservable
			.Subscribe(frames =>
				_ = RunBackgroundAsync(() => EnqueueAudio(DateTime.Now, AudioSourceType.Microphone, frames), tracked: true))
			.AddTo(_observableHandlers);

		_captureSession.ApplicationRecordingObservable
			.Subscribe(frames =>
				_ = RunBackgroundAsync(() => EnqueueAudio(DateTime.Now, AudioSourceType.Application, frames), tracked: true))
			.AddTo(_observableHandlers);
	}

	public ValueTask DisposeAsync()
	{
		lock (_trackedTasksLock)
		{
			return new ValueTask(_disposeTask ??= DisposeCoreAsync());
		}
	}

	private async Task DisposeCoreAsync()
	{
		_disposables.Remove(_observableHandlers);

		lock (_trackedTasksLock)
		{
			_isDisposed = true;
		}

		await ClearQueuesInternal();
		await _cancellationTokenSource.CancelAsync();

		Task[] trackedTasks;
		lock (_trackedTasksLock)
		{
			trackedTasks = _trackedTasks.ToArray();
		}

		var completion = Task.WhenAll(trackedTasks);
		try
		{
			await completion;
		}
		catch (OperationCanceledException) when (completion.IsCanceled)
		{
		}
		finally
		{
			_disposables.Dispose();
		}
	}

	public async Task InitializeAsync(CancellationToken cancellationToken = default)
	{
		if (_isDisposed)
		{
			throw new ObjectDisposedException(nameof(ApplicationController));
		}

		using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource.Token, cancellationToken);
		await RunTrackedAsync(() => _llmConnector.InitializeAsync(cts.Token));
		await RunTrackedAsync(() => _transcriber.InitializeAsync(cts.Token));
		await RunTrackedAsync(() => _textProcessor.InitializeAsync(cts.Token));
		await RunTrackedAsync(() => _captureSession.InitializeAsync(cts.Token));
	}

	public Task<(int recordsCleared, int transcriptionsCleared)> ClearQueues(CancellationToken cancellationToken = default)
	{
		if (_isDisposed)
		{
			throw new ObjectDisposedException(nameof(ApplicationController));
		}

		return ClearQueuesInternal(cancellationToken);
	}

	public async Task BeginSessionAsync(CancellationToken cancellationToken = default)
	{
		await DrainAsync(cancellationToken);
		if (_textProcessor is ISessionTextProcessor processor)
		{
			await processor.BeginSessionAsync(cancellationToken);
		}
	}

	// Stop the audio sources before calling this method so that no new records arrive.
	public async Task CompleteSessionAsync(CancellationToken cancellationToken = default)
	{
		await DrainAsync(cancellationToken);
		if (_textProcessor is ISessionTextProcessor processor)
		{
			await processor.CompleteSessionAsync(cancellationToken);
		}
	}

	private async Task DrainAsync(CancellationToken cancellationToken)
	{
		while (true)
		{
			cancellationToken.ThrowIfCancellationRequested();
			Task[] pending;
			lock (_trackedTasksLock)
			{
				ObjectDisposedException.ThrowIf(_isDisposed, this);
				pending = _trackedTasks.Where(task => !task.IsCompleted).ToArray();
			}
			if (pending.Length == 0)
			{
				return;
			}
			await Task.WhenAll(pending).WaitAsync(cancellationToken);
		}
	}

	private async Task<(int recordsCleared, int transcriptionsCleared)> ClearQueuesInternal(CancellationToken cancellationToken = default)
	{
		int recordsCleared, transcriptionsCleared;
		using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource.Token, cancellationToken);
		await _audioRecordsSemaphore.WaitAsync(cts.Token);
		try
		{
			recordsCleared = _audioRecordsQueue.Count;
			_audioRecordsQueue.Clear();
		}
		finally
		{
			_audioRecordsSemaphore.Release();
		}

		await _textRecordsSemaphore.WaitAsync(cts.Token);
		try
		{
			transcriptionsCleared = _textRecordsQueue.Count;
			_textRecordsQueue.Clear();
		}
		finally
		{
			_textRecordsSemaphore.Release();
		}

		return (recordsCleared, transcriptionsCleared);
	}

	private Task RunBackgroundAsync(Func<Task> operation, bool tracked = false,
		[CallerArgumentExpression(nameof(operation))] string? operationName = null)
	{
		Task ObserveAsync() => BackgroundOperation.RunAsync(operation, _logger,
			operationName ?? "Unknown operation", _stoppingToken);

		return tracked ? RunTrackedAsync(ObserveAsync) : ObserveAsync();
	}

	private Task RunTrackedAsync(Func<Task> operation)
	{
		Task task;
		lock (_trackedTasksLock)
		{
			if (_isDisposed)
			{
				return Task.CompletedTask;
			}

			task = operation();
			_trackedTasks = _trackedTasks.Add(task);
		}

		task.ContinueWith(_ =>
		{
			lock (_trackedTasksLock)
			{
				_trackedTasks = _trackedTasks.Remove(task);
			}
		}, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

		return task;
	}

	private async Task EnqueueAudio(DateTime ts, AudioSourceType source, IReadOnlyList<AudioFrame> frames)
	{
		if (_isDisposed)
		{
			return;
		}

		await _audioRecordsSemaphore.WaitAsync(_cancellationTokenSource.Token);
		try
		{
			while (_audioRecordsQueue.Count >= _settings.TranscriptionQueueCapacity
				&& _audioRecordsQueue.TryDequeue(out var removed))
			{
				_logger.LogWarning("Audio queue is full. Removed 1 oldest pending recording ({Source}, {Timestamp}).", removed.source, removed.ts);
			}
			_audioRecordsQueue.Enqueue((ts, source, frames));
		}
		finally
		{
			_audioRecordsSemaphore.Release();

			if (_transcriberSemaphore.Wait(0))
			{
				await DequeAudio(true);
			}
		}
	}

	private async Task DequeAudio(bool transcribeImmediate)
	{
		if (_isDisposed)
		{
			return;
		}

		(DateTime ts, AudioSourceType source, IReadOnlyList<AudioFrame> frames) rec;
		await _audioRecordsSemaphore.WaitAsync(_cancellationTokenSource.Token);
		try
		{
			if (!_audioRecordsQueue.TryDequeue(out rec))
			{
				if (transcribeImmediate)
				{
					Debug.Assert(_transcriberSemaphore.CurrentCount < _settings.MaxTranscriptionProcessingCount);
					_transcriberSemaphore.Release();
				}

				return;
			}
		}
		finally
		{
			_audioRecordsSemaphore.Release();
		}

		_ = RunBackgroundAsync(() => transcribeImmediate
			? TranscribeImmediate(rec.ts, rec.source, rec.frames)
			: Transcribe(rec.ts, rec.source, rec.frames), tracked: true);
	}

	private async Task EnqueueText(DateTime ts, AudioSourceType source, string text)
	{
		if (_isDisposed)
		{
			return;
		}

		await _textRecordsSemaphore.WaitAsync(_cancellationTokenSource.Token);
		try
		{
			while (_textRecordsQueue.Count >= _settings.TranscriptionQueueCapacity)
			{
				_textRecordsQueue.Remove(_textRecordsQueue.First());
				_logger.LogWarning("Text queue is full. Removed 1 oldest pending transcription.");
			}
			_textRecordsQueue.Add((ts, source, text));
		}
		finally
		{
			_textRecordsSemaphore.Release();

			if (_textProcessorSemaphore.Wait(0))
			{
				await DequeText(true);
			}
		}
	}

	private async Task DequeText(bool processImmediate)
	{
		if (_isDisposed)
		{
			return;
		}

		(DateTime ts, AudioSourceType source, string text) rec;
		await _textRecordsSemaphore.WaitAsync(_cancellationTokenSource.Token);
		try
		{
			if (_textRecordsQueue.Count == 0)
			{
				if (processImmediate)
				{
					Debug.Assert(_textProcessorSemaphore.CurrentCount < _settings.MaxTextProcessingCount);
					_textProcessorSemaphore.Release();
				}

				return;
			}

			rec = _textRecordsQueue.First();
			_textRecordsQueue.Remove(rec);
		}
		finally
		{
			_textRecordsSemaphore.Release();
		}

		_ = RunBackgroundAsync(() => processImmediate
			? ProcessTextImmediate(rec.ts, rec.source, rec.text)
			: ProcessText(rec.ts, rec.source, rec.text), tracked: true);
	}

	private async Task Transcribe(DateTime ts, AudioSourceType source, IReadOnlyList<AudioFrame> frames)
	{
		Debug.Assert(_transcriber.IsReady, "Transcriber isn't initialized.");

		await _transcriberSemaphore.WaitAsync(_cancellationTokenSource.Token);
		await TranscribeImmediate(ts, source, frames);
	}

	private async Task TranscribeImmediate(DateTime ts, AudioSourceType source, IReadOnlyList<AudioFrame> frames)
	{
		Debug.Assert(_transcriberSemaphore.CurrentCount < _settings.MaxTranscriptionProcessingCount);

		try
		{
			var text = await Task.Run(() => _transcriber.TranscribeAsync(frames, _cancellationTokenSource.Token), _cancellationTokenSource.Token);
			await EnqueueText(ts, source, text);
		}
		catch (OperationCanceledException) when (_cancellationTokenSource.IsCancellationRequested)
		{
		}
		catch (Exception exception)
		{
			_logger.LogError(exception, "Transcription failed");
		}
		finally
		{
			_transcriberSemaphore.Release();
			_ = RunBackgroundAsync(() => DequeAudio(false), tracked: true);
		}
	}

	private async Task ProcessText(DateTime ts, AudioSourceType source, string text)
	{
		Debug.Assert(_textProcessor.IsReady, "Text processor isn't initialized.");

		await _textProcessorSemaphore.WaitAsync(_cancellationTokenSource.Token);
		await ProcessTextImmediate(ts, source, text);
	}

	private async Task ProcessTextImmediate(DateTime ts, AudioSourceType source, string text)
	{
		Debug.Assert(_textProcessorSemaphore.CurrentCount < _settings.MaxTextProcessingCount);

		try
		{
			await _textProcessor.ProcessAsync(ts, source, text, _stoppingToken);
		}
		catch (OperationCanceledException) when (_cancellationTokenSource.IsCancellationRequested)
		{
		}
		catch (Exception exception)
		{
			_logger.LogError(exception, "Process text failed");
		}
		finally
		{
			_textProcessorSemaphore.Release();
			_ = RunBackgroundAsync(() => DequeText(false), tracked: true);
		}
	}

	private class TextRecordComparer : IComparer<(DateTime ts, AudioSourceType source, string text)>
	{
		private readonly Comparer<DateTime> _comparer = Comparer<DateTime>.Default;

		public int Compare((DateTime ts, AudioSourceType source, string text) x,
			(DateTime ts, AudioSourceType source, string text) y)
		{
			return _comparer.Compare(x.ts, y.ts);
		}
	}
}
