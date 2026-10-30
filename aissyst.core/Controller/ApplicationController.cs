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

namespace aissyst.core.Controller;

public class ApplicationController : IAsyncDisposable
{
	private readonly ITranscriber _transcriber;
	private readonly DialogCoordinator _dialog;
	private readonly ICaptureSession _captureSession;
	private readonly ILogger<ApplicationController> _logger;
	private readonly ILLMConnector _llmConnector;
	private readonly SessionModel? _sessionModel;
	private readonly IOutput _output;
	private readonly IApplicationSettings _settings;
	private readonly ISessionSelector _sessionSelector;

	private readonly CancellationTokenSource _cancellationTokenSource;
	private readonly CancellationToken _stoppingToken;

	private readonly Lock _trackedTasksLock = new();
	private readonly Lock _outputSync = new();
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
	private int _lastPrintedAnswerId;
	private bool _requestInProgress;

	public ApplicationController(ITranscriber transcriber, DialogCoordinator dialog,
		ICaptureSession captureSession, ILogger<ApplicationController> logger, ILLMConnector llmConnector,
		IOutput output, IApplicationSettings settings, ISessionSelector sessionSelector,
		SessionModel? sessionModel = null)
	{
		_transcriber = transcriber;
		_dialog = dialog;
		_captureSession = captureSession;
		_logger = logger;
		_llmConnector = llmConnector;
		_sessionModel = sessionModel;
		_output = output;
		_settings = settings;
		_sessionSelector = sessionSelector;
		_dialog.MessageCompleted += PrintDialogMessage;
		_dialog.HistoryReplaced += PrintDialogHistory;

		Debug.Assert(settings is { MaxTextProcessingCount: > 0, MaxTranscriptionProcessingCount: > 0 });

		_cancellationTokenSource = new CancellationTokenSource();
		_stoppingToken = _cancellationTokenSource.Token;

		_audioRecordsSemaphore = new SemaphoreSlim(1, 1);
		_textRecordsSemaphore = new SemaphoreSlim(1, 1);
		_transcriberSemaphore = new SemaphoreSlim(settings.MaxTranscriptionProcessingCount, settings.MaxTranscriptionProcessingCount);
		_textProcessorSemaphore = new SemaphoreSlim(settings.MaxTextProcessingCount, settings.MaxTextProcessingCount);

		_captureSession.MicrophoneRecordingCompleted += OnMicrophoneRecordingCompleted;
		_captureSession.ApplicationRecordingCompleted += OnApplicationRecordingCompleted;
	}

	private void OnMicrophoneRecordingCompleted(IReadOnlyList<AudioFrame> frames)
	{
		_ = RunBackgroundAsync(() => EnqueueAudio(DateTime.UtcNow, AudioSourceType.Microphone, frames), tracked: true);
	}

	private void OnApplicationRecordingCompleted(IReadOnlyList<AudioFrame> frames)
	{
		_ = RunBackgroundAsync(() => EnqueueAudio(DateTime.UtcNow, AudioSourceType.Application, frames), tracked: true);
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
		lock (_trackedTasksLock)
		{
			_isDisposed = true;
		}

		_captureSession.MicrophoneRecordingCompleted -= OnMicrophoneRecordingCompleted;
		_captureSession.ApplicationRecordingCompleted -= OnApplicationRecordingCompleted;
		_dialog.MessageCompleted -= PrintDialogMessage;
		_dialog.HistoryReplaced -= PrintDialogHistory;

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
			_audioRecordsSemaphore.Dispose();
			_textRecordsSemaphore.Dispose();
			_transcriberSemaphore.Dispose();
			_textProcessorSemaphore.Dispose();
			_cancellationTokenSource.Dispose();
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
			await RunTrackedAsync(() => _dialog.InitializeAsync(cts.Token));
		}

		await RunTrackedAsync(() => _captureSession.InitializeAsync(cts.Token));

		PrintDialogHistory(_dialog.GetDialog(DateTime.MinValue));
	}

	private void PrintDialogHistory(IReadOnlyList<DialogMessage> dialog)
	{
		lock (_outputSync)
		{
			_output.Clear();
			_output.PrintMultipleDialogMessages(dialog);
		}
	}

	private void PrintDialogMessage(DialogMessage message)
	{
		lock (_outputSync)
		{
			_output.PrintDialogMessage(message);
		}
	}

	public async Task<DialogResponse?> SendRequestAsync(RequestType request, CancellationToken cancellationToken = default,
		Func<CancellationToken, Task<IDisposable>>? pauseAudioProcessing = null)
	{
		CancellationToken requestToken = default;
		try
		{
			Task<DialogResponse?> operation;
			lock (_trackedTasksLock)
			{
				ObjectDisposedException.ThrowIf(_isDisposed, this);
				cancellationToken.ThrowIfCancellationRequested();
				if (_requestInProgress || _llmConnector.IsBusy)
				{
					throw new InvalidOperationException("An assistance request is already in progress. Wait for it to finish.");
				}
				var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_stoppingToken, cancellationToken);
				requestToken = cancellation.Token;
				_requestInProgress = true;
				operation = SendRequestCoreAsync(request, cancellation, pauseAudioProcessing);
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

	private async Task<DialogResponse?> SendRequestCoreAsync(RequestType request,
		CancellationTokenSource cancellation, Func<CancellationToken, Task<IDisposable>>? pauseAudioProcessing)
	{
		try
		{
			var model = _sessionModel ?? throw new InvalidOperationException("The session model is not configured.");
			Task<DialogResponse?> response;
			IDisposable? pause = null;
			try
			{
				if (pauseAudioProcessing != null)
				{
					pause = await pauseAudioProcessing(cancellation.Token);
				}
				await WaitForIdleAsync(cancellation.Token);
				cancellation.Token.ThrowIfCancellationRequested();
				// The model snapshots the dialog synchronously before returning the response task.
				response = model.SendRequestAsync(request, cancellation.Token);
			}
			finally
			{
				pause?.Dispose();
			}
			var result = await response;
			cancellation.Token.ThrowIfCancellationRequested();
			if (result != null && !string.IsNullOrWhiteSpace(result.Message))
			{
				lock (_outputSync)
				{
					// Preserve display order if continuations delivering accepted answers are scheduled out of order.
					if (result.RequestId > _lastPrintedAnswerId)
					{
						_output.PrintLLMAnswer(result.Message);
						_lastPrintedAnswerId = result.RequestId;
					}
				}
			}
			return result;
		}
		finally
		{
			lock (_trackedTasksLock)
			{
				_requestInProgress = false;
			}
			cancellation.Dispose();
		}
	}

	private static async Task ObserveRequestCompletionAsync(Task<DialogResponse?> operation)
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

			if (!_textRecordsQueue.Add(message))
			{
				_logger.LogWarning("A pending transcription with timestamp {Timestamp:O} already exists. The incoming transcription from {Source} was not queued.",
					message.Timestamp, message.Source);
			}
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


		await _textProcessorSemaphore.WaitAsync(_cancellationTokenSource.Token);
		await ProcessTextImmediate(message);
	}

	private async Task ProcessTextImmediate(DialogMessage message)
	{
		Debug.Assert(_textProcessorSemaphore.CurrentCount < _settings.MaxTextProcessingCount);

		try
		{
			await _dialog.ProcessAsync(message, _stoppingToken);
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
}
