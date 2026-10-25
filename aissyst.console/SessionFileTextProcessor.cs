using System.Text;
using aissyst.core;
using aissyst.core.Processor;
using Microsoft.Extensions.Logging;

namespace aissyst.console;

public sealed class SessionFileTextProcessor : ITextProcessor
{
	private readonly string _outputDirectory;
	private readonly ILogger<SessionFileTextProcessor> _logger;
	private readonly Lock _sync = new();
	private readonly List<DialogMessage> _dialog = [];
	private bool _initialized;
	private bool _saving;
	private string? _sessionDirectory;
	private DialogMessage? _pendingMessage;

	public SessionFileTextProcessor(string outputDirectory, ILogger<SessionFileTextProcessor> logger)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
		_outputDirectory = Path.GetFullPath(outputDirectory);
		_logger = logger;
	}

	public bool IsReady
	{
		get
		{
			lock (_sync)
			{
				return _initialized;
			}
		}
	}

	public Task InitializeAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		lock (_sync)
		{
			_initialized = true;
		}
		return Task.CompletedTask;
	}

	public Task BeginSessionAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		lock (_sync)
		{
			EnsureInitialized();
			if (_sessionDirectory != null)
			{
				throw new InvalidOperationException("Complete the current transcription session before starting another one.");
			}
			_sessionDirectory = Path.Combine(_outputDirectory, $"session-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
			_pendingMessage = null;
		}
		return Task.CompletedTask;
	}

	public DialogMessage? Process(DialogMessage message)
	{
		var normalized = string.Join(" ", message.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
		lock (_sync)
		{
			EnsureInitialized();
			if (_sessionDirectory == null || _saving)
			{
				throw new InvalidOperationException("There is no active transcription session accepting text.");
			}
			if (message.Source is not (AudioSourceType.Microphone or AudioSourceType.Application))
			{
				throw new ArgumentOutOfRangeException(nameof(message.Source), "A single supported audio source is required.");
			}
			if (normalized.Length == 0)
			{
				return null;
			}
			var next = message with { Text = normalized };
			DialogMessage? completed = null;
			if (_pendingMessage is { } pending && pending.Source == next.Source)
			{
				_pendingMessage = next with { Text = pending.Text + " " + next.Text };
			}
			else
			{
				completed = _pendingMessage;
				_pendingMessage = next;
			}
			_dialog.Add(next);
			return completed;
		}
	}

	public async Task CompleteSessionAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		string directory;
		string microphone;
		string application;
		lock (_sync)
		{
			EnsureInitialized();
			if (_sessionDirectory == null)
			{
				return;
			}
			if (_saving)
			{
				throw new InvalidOperationException("The transcription session is already being saved.");
			}
			directory = _sessionDirectory;
			// OrderBy is stable, so recordings with identical timestamps are retained.
			microphone = string.Join(" ", _dialog.Where(message => message.Source == AudioSourceType.Microphone)
				.OrderBy(message => message.Timestamp).Select(message => message.Text));
			application = string.Join(" ", _dialog.Where(message => message.Source == AudioSourceType.Application)
				.OrderBy(message => message.Timestamp).Select(message => message.Text));
			_saving = true;
		}
		try
		{
			Directory.CreateDirectory(directory);
			await File.WriteAllTextAsync(Path.Combine(directory, "microphone.txt"), microphone, new UTF8Encoding(false), cancellationToken);
			await File.WriteAllTextAsync(Path.Combine(directory, "application.txt"), application, new UTF8Encoding(false), cancellationToken);
			lock (_sync)
			{
				_dialog.Clear();
				_sessionDirectory = null;
				_pendingMessage = null;
			}
			_logger.LogInformation("Session transcripts saved to {Directory}.", directory);
		}
		finally
		{
			lock (_sync)
			{
				_saving = false;
			}
		}
	}

	private void EnsureInitialized()
	{
		if (!_initialized)
		{
			throw new InvalidOperationException("The text processor has not been initialized.");
		}
	}

	public IReadOnlyList<DialogMessage> GetDialog(DateTime fromTime)
	{
		DialogMessage[] snapshot;
		lock (_sync)
		{
			snapshot = _dialog.ToArray();
		}
		return snapshot.ToDialog(fromTime);
	}
}
