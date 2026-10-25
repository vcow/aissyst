using System.Collections.Immutable;
using aissyst.core.Database;
using aissyst.core.LLMConnector.Model;
using Microsoft.Extensions.Logging;

namespace aissyst.core.Processor;

public sealed class TextProcessor : ITextProcessor, IAsyncDisposable
{
	private readonly ISessionSelector _sessionSelector;
	private readonly IDatabase _database;
	private readonly ILogger<TextProcessor> _logger;
	private readonly Lock _sync = new();
	private readonly CancellationTokenSource _shutdown = new();
	private ImmutableList<DialogMessage> _dialog = [];
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

	private async Task LoadDialogAsync(CancellationToken cancellationToken)
	{
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
		var token = linked.Token;
		try
		{
			var selectedId = _sessionSelector.GetSelectedSessionId();
			var session = await _database.GetSessionAsync(selectedId, token);
			var dialog = session.Dialog.OrderBy(record => record.Timestamp).Select(record => new DialogMessage(
				record.Timestamp, record.Role?.Trim().ToLowerInvariant() switch
				{
					"interviewer" => AudioSourceType.Application,
					"interviewee" => AudioSourceType.Microphone,
					_ => AudioSourceType.None
				}, record.Message)).ToImmutableList();
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
	}

	public DialogMessage? Process(DialogMessage message)
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

			_dialog = _dialog.Add(normalized);
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
			_shutdown.Dispose();
		}
	}
}