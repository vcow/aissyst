using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using aissyst.core.Audio;
using aissyst.core.LLMConnector;
using aissyst.core.LLMConnector.DTO;
using aissyst.core.LLMConnector.Model;
using aissyst.core.Output;
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
	private readonly SessionModel? _sessionModel;
	private readonly IOutput _output;
	private readonly IApplicationSettings _settings;
	private readonly ISessionSelector _sessionSelector;

	private readonly CompositeDisposable _disposables = new();

	private readonly CompositeDisposable _observableHandlers;
	private readonly CancellationTokenSource _cancellationTokenSource;
	private readonly CancellationToken _stoppingToken;

	private readonly Lock _trackedTasksLock = new();
	private readonly Lock _textProcessingLock = new();
	private ImmutableHashSet<Task> _trackedTasks = new HashSet<Task>().ToImmutableHashSet();
	private ImmutableHashSet<Task> _trackedRequestTasks = ImmutableHashSet<Task>.Empty;

	private readonly ConcurrentQueue<(DateTime ts, AudioSourceType source, IReadOnlyList<AudioFrame> frames)> _audioRecordsQueue = new();
	private readonly SortedSet<DialogMessage> _textRecordsQueue = new(new DialogMessageComparer());

	private readonly SemaphoreSlim _audioRecordsSemaphore;
	private readonly SemaphoreSlim _textRecordsSemaphore;
	private readonly SemaphoreSlim _transcriberSemaphore;
	private readonly SemaphoreSlim _textProcessorSemaphore;

	private bool _isDisposed;
	private Task? _disposeTask;
	private CancellationTokenSource? _requestCancellation;

	public ApplicationController(ITranscriber transcriber, ITextProcessor textProcessor,
		ICaptureSession captureSession, ILogger<ApplicationController> logger, ILLMConnector llmConnector,
		IOutput output, IApplicationSettings settings, ISessionSelector sessionSelector,
		SessionModel? sessionModel = null)
	{
		_transcriber = transcriber;
		_textProcessor = textProcessor;
		_captureSession = captureSession;
		_logger = logger;
		_llmConnector = llmConnector;
		_sessionModel = sessionModel;
		_output = output;
		_settings = settings;
		_sessionSelector = sessionSelector;

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
			trackedTasks = _trackedTasks.Concat(_trackedRequestTasks).ToArray();
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
		await RunTrackedAsync(() => _sessionSelector.InitializeAsync(cts.Token));
		await RunTrackedAsync(() => _llmConnector.InitializeAsync(cts.Token));
		await RunTrackedAsync(() => _transcriber.InitializeAsync(cts.Token));
		if (_sessionModel != null)
		{
			await RunTrackedAsync(() => _sessionModel.InitializeAsync(cts.Token));
		}
		else
		{
			await RunTrackedAsync(() => _textProcessor.InitializeAsync(cts.Token));
		}

		await RunTrackedAsync(() => _captureSession.InitializeAsync(cts.Token));

		var dialog = _textProcessor.GetDialog(DateTime.MinValue);
		foreach (var message in dialog)
		{
			_output.PrintDialogMessage(message);
		}
	}

	public async Task<DialogResponse> SendRequestAsync(RequestType request, CancellationToken cancellationToken = default)
	{
		CancellationToken requestToken = default;
		try
		{
			Task<DialogResponse> operation;
			lock (_trackedTasksLock)
			{
				ObjectDisposedException.ThrowIf(_isDisposed, this);
				cancellationToken.ThrowIfCancellationRequested();
				var previous = _requestCancellation;
				var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_stoppingToken, cancellationToken);
				requestToken = cancellation.Token;
				_requestCancellation = cancellation;
				var previousCancellation = previous?.CancelAsync() ?? Task.CompletedTask;
				operation = SendRequestCoreAsync(request, previousCancellation, cancellation);
				_ = RunTrackedAsync(() => ObserveRequestCompletionAsync(operation), isRequest: true);
			}
			return await operation;
		}
		catch (OperationCanceledException) when (requestToken.IsCancellationRequested || cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception)
		{
			_logger.LogError(exception, "Failed to process the LLM assistance request.");
			throw;
		}
	}

	private async Task<DialogResponse> SendRequestCoreAsync(RequestType request, Task previousCancellation,
		CancellationTokenSource cancellation)
	{
		try
		{
			await previousCancellation;
			var cancellationToken = cancellation.Token;
			cancellationToken.ThrowIfCancellationRequested();
			var model = _sessionModel ?? throw new InvalidOperationException("The session model is not configured.");
			var payload = model.GetRequest(request);
			var result = await _llmConnector.SendRequest(payload, cancellationToken);
			lock (_trackedTasksLock)
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (!string.IsNullOrWhiteSpace(result.Error))
				{
					_logger.LogError("LLM request failed: {Error}", result.Error);
					return result;
				}
				if (result.RequestId != payload.RequestId)
				{
					throw new InvalidDataException("The LLM response does not match the assistance request.");
				}
				model.Reset(result);
				if (!string.IsNullOrWhiteSpace(result.Message))
				{
					_output.PrintLLMAnswer(result.Message);
				}
				return result;
			}
		}
		finally
		{
			lock (_trackedTasksLock)
			{
				if (ReferenceEquals(_requestCancellation, cancellation))
				{
					_requestCancellation = null;
				}
			}
			cancellation.Dispose();
		}
	}

	private static async Task ObserveRequestCompletionAsync(Task<DialogResponse> operation)
	{
		try
		{
			await operation;
		}
		catch (Exception)
		{
			// SendRequestAsync logs failures; disposal only needs to wait for completion.
		}
	}

	public Task<(int recordsCleared, int transcriptionsCleared)> ClearQueues(CancellationToken cancellationToken = default)
	{
		if (_isDisposed)
		{
			throw new ObjectDisposedException(nameof(ApplicationController));
		}

		return ClearQueuesInternal(cancellationToken);
	}

	// Stop the audio sources first if all pending work must finish before returning.
	public async Task WaitForIdleAsync(CancellationToken cancellationToken = default)
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

	private Task RunTrackedAsync(Func<Task> operation, bool isRequest = false)
	{
		Task task;
		lock (_trackedTasksLock)
		{
			if (_isDisposed)
			{
				return Task.CompletedTask;
			}

			task = operation();
			if (isRequest)
			{
				_trackedRequestTasks = _trackedRequestTasks.Add(task);
			}
			else
			{
				_trackedTasks = _trackedTasks.Add(task);
			}
		}

		task.ContinueWith(_ =>
		{
			lock (_trackedTasksLock)
			{
				if (isRequest)
				{
					_trackedRequestTasks = _trackedRequestTasks.Remove(task);
				}
				else
				{
					_trackedTasks = _trackedTasks.Remove(task);
				}
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

	private async Task EnqueueText(DialogMessage message)
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

			_textRecordsQueue.Add(message);
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

		DialogMessage message;
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

			message = _textRecordsQueue.First();
			_textRecordsQueue.Remove(message);
		}
		finally
		{
			_textRecordsSemaphore.Release();
		}

		_ = RunBackgroundAsync(() => processImmediate
			? ProcessTextImmediate(message)
			: ProcessText(message), tracked: true);
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
			await EnqueueText(new DialogMessage(ts, source, text));
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

	private async Task ProcessText(DialogMessage message)
	{
		Debug.Assert(_textProcessor.IsReady, "Text processor isn't initialized.");

		await _textProcessorSemaphore.WaitAsync(_cancellationTokenSource.Token);
		await ProcessTextImmediate(message);
	}

	private Task ProcessTextImmediate(DialogMessage message)
	{
		Debug.Assert(_textProcessorSemaphore.CurrentCount < _settings.MaxTextProcessingCount);

		try
		{
			lock (_textProcessingLock)
			{
				var output = _textProcessor.Process(message);
				if (output != null)
				{
					_output.PrintDialogMessage(output);
				}
			}
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

		return Task.CompletedTask;
	}
}
