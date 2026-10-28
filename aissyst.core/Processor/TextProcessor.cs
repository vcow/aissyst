using System.Collections.Immutable;
using aissyst.core.Database;
using aissyst.core.LLMConnector.Model;
using Microsoft.Extensions.Logging;

namespace aissyst.core.Processor;

public sealed class TextProcessor : IReloadableTextProcessor, IAsyncDisposable
{
	private readonly ISessionSelector _sessionSelector;
	private readonly IDatabase _database;
	private readonly ILogger<TextProcessor> _logger;
	private readonly Lock _sync = new();
	private readonly SemaphoreSlim _storageGate = new(1, 1);
	private readonly CancellationTokenSource _shutdown = new();
	private ImmutableList<DialogMessage> _dialog = [];
	private readonly List<DialogMessage> _unsaved = [];
	private DialogMessage? _pendingMessage;
	private Task? _initialization;
	private Task? _disposal;
	private bool _isReady;

	public TextProcessor(ISessionSelector sessionSelector, IDatabase database, ILogger<TextProcessor> logger)
	{
		_sessionSelector = sessionSelector;
		_database = database;
		_logger = logger;
	}

	public bool IsReady
	{
		get
		{
			lock (_sync)
			{
				return _isReady;
			}
		}
	}

	public Task InitializeAsync(CancellationToken cancellationToken = default)
	{
		lock (_sync)
		{
			ObjectDisposedException.ThrowIf(_disposal != null, this);
			cancellationToken.ThrowIfCancellationRequested();
			if (_isReady)
			{
				return Task.CompletedTask;
			}

			if (_initialization is { IsCompleted: false })
			{
				return _initialization.WaitAsync(cancellationToken);
			}

			_initialization = LoadDialogAsync(cancellationToken);
			return _initialization.WaitAsync(cancellationToken);
		}
	}

	public async Task ReloadAsync(CancellationToken cancellationToken = default)
	{
		lock (_sync)
		{
			ObjectDisposedException.ThrowIf(_disposal != null, this);
			if (!_isReady)
			{
				throw new InvalidOperationException("The text processor has not been initialized.");
			}
		}
		await LoadDialogAsync(cancellationToken).ConfigureAwait(false);
	}

	private async Task LoadDialogAsync(CancellationToken cancellationToken)
	{
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
		var token = linked.Token;
		await _storageGate.WaitAsync(token).ConfigureAwait(false);
		try
		{
			var selectedId = _sessionSelector.GetSelectedSessionId();
			var session = await _database.GetSessionAsync(selectedId, token).ConfigureAwait(false);
			var dialog = session.Dialog.OrderBy(record => record.Timestamp).Select(record => new DialogMessage(
				record.Timestamp, record.Role?.Trim().ToLowerInvariant() switch
				{
					"interviewer" => AudioSourceType.Application,
					"interviewee" => AudioSourceType.Microphone,
					_ => AudioSourceType.None
				}, record.Message)).Concat(_unsaved).OrderBy(message => message.Timestamp).ToImmutableList();
			lock (_sync)
			{
				token.ThrowIfCancellationRequested();
				_dialog = dialog;
				// Restored messages are displayed as history, not emitted again as new turns.
				_pendingMessage = null;
				_isReady = true;
			}

			_logger.LogInformation("Loaded {Count} dialog records for session {SessionId}.", dialog.Count, selectedId);
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception)
		{
			_logger.LogError(exception, "Failed to initialize the text processor.");
			throw;
		}
		finally
		{
			_storageGate.Release();
		}
	}

	public DialogMessage? Process(DialogMessage message) => ProcessAsync(message).GetAwaiter().GetResult();

	public async Task<DialogMessage?> ProcessAsync(DialogMessage message, CancellationToken cancellationToken = default)
	{
		lock (_sync)
		{
			ObjectDisposedException.ThrowIf(_disposal != null, this);
			if (!_isReady)
			{
				throw new InvalidOperationException("The text processor has not been initialized.");
			}
		}
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
		await _storageGate.WaitAsync(linked.Token).ConfigureAwait(false);
		try
		{
			int sessionId;
			lock (_sync)
			{
				ObjectDisposedException.ThrowIf(_disposal != null, this);
				if (!_isReady)
				{
					throw new InvalidOperationException("The text processor has not been initialized.");
				}
				sessionId = _sessionSelector.GetSelectedSessionId();
			}
			var fragment = message with
			{
				Timestamp = message.Timestamp.Kind == DateTimeKind.Local
					? message.Timestamp.ToUniversalTime()
					: DateTime.SpecifyKind(message.Timestamp, DateTimeKind.Utc)
			};
			_unsaved.Add(fragment);
			var completed = ProcessCore(fragment);
			await TryFlushAsync(sessionId, linked.Token).ConfigureAwait(false);
			return completed;
		}
		catch (OperationCanceledException) when (linked.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception)
		{
			_logger.LogError(exception, "Failed to save or process a dialog fragment.");
			throw;
		}
		finally
		{
			_storageGate.Release();
		}
	}

	private async Task TryFlushAsync(int sessionId, CancellationToken cancellationToken)
	{
		if (_unsaved.Count == 0)
		{
			return;
		}
		try
		{
			await _database.AddSessionMultipleDialogRecordsAsync(sessionId, _unsaved.ToArray(), cancellationToken).ConfigureAwait(false);
			_unsaved.Clear();
		}
		catch (Exception exception)
		{
			_logger.LogError(exception, "Failed to save {Count} dialog fragments. They remain in memory for the next save attempt.", _unsaved.Count);
		}
	}

	private DialogMessage? ProcessCore(DialogMessage message)
	{
		lock (_sync)
		{
			ObjectDisposedException.ThrowIf(_disposal != null, this);
			if (!_isReady)
			{
				throw new InvalidOperationException("The text processor has not been initialized.");
			}

			if (string.IsNullOrWhiteSpace(message.Text))
			{
				_dialog = _dialog.Add(message);
				return null;
			}

			DialogMessage? completed = null;
			var normalized = message with { Text = message.Text.Trim() };
			if (_pendingMessage is { } pending && pending.Source == message.Source)
			{
				_pendingMessage = normalized with { Text = pending.Text + " " + normalized.Text };
			}
			else
			{
				completed = _pendingMessage;
				_pendingMessage = normalized;
			}

			_dialog = _dialog.Add(message);
			if (completed != null)
			{
				_logger.LogDebug("Completed a dialog message from {Source}.", completed.Source);
			}

			return completed;
		}
	}

	public IReadOnlyList<DialogMessage> GetDialog(DateTime fromTime) => Volatile.Read(ref _dialog).ToDialog(fromTime);

	public ValueTask DisposeAsync()
	{
		lock (_sync)
		{
			return new ValueTask(_disposal ??= DisposeCoreAsync());
		}
	}

	private async Task DisposeCoreAsync()
	{
		_isReady = false;
		await _shutdown.CancelAsync();
		try
		{
			if (_initialization != null)
			{
				await _initialization;
			}
		}
		catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
		{
		}
		catch (Exception)
		{
			// LoadDialogAsync already logged initialization failures.
		}
		finally
		{
			// Wait until an in-flight save or reload has released the storage gate.
			await _storageGate.WaitAsync().ConfigureAwait(false);
			try
			{
				using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
				if (_unsaved.Count > 0)
				{
					await TryFlushAsync(_sessionSelector.GetSelectedSessionId(), timeout.Token).ConfigureAwait(false);
				}
			}
			finally
			{
				_storageGate.Release();
				_shutdown.Dispose();
			}
		}
	}
}