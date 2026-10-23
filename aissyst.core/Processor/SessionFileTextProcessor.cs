using System.Text;
using Microsoft.Extensions.Logging;

namespace aissyst.core.Processor;

public sealed class SessionFileTextProcessor : ISessionTextProcessor
{
	private readonly string _outputDirectory;
	private readonly ILogger<SessionFileTextProcessor> _logger;
	private readonly Lock _sync = new();
	private readonly List<(DateTime Timestamp, string Text)> _microphone = [];
	private readonly List<(DateTime Timestamp, string Text)> _application = [];
	private bool _initialized;
	private bool _saving;
	private string? _sessionDirectory;

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
		}
		return Task.CompletedTask;
	}

	public Task ProcessAsync(DateTime timestamp, AudioSourceType source, string text,
		CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var normalized = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
		lock (_sync)
		{
			EnsureInitialized();
			if (_sessionDirectory == null || _saving)
			{
				throw new InvalidOperationException("There is no active transcription session accepting text.");
			}
			var records = source switch
			{
				AudioSourceType.Microphone => _microphone,
				AudioSourceType.Application => _application,
				_ => throw new ArgumentOutOfRangeException(nameof(source), "A single supported audio source is required.")
			};
			if (normalized.Length != 0)
			{
				records.Add((timestamp, normalized));
			}
		}
		if (normalized.Length != 0)
		{
			_logger.LogInformation("{Source} [{Timestamp:HH:mm:ss}]: {Text}", source, timestamp, normalized);
		}
		return Task.CompletedTask;
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
			microphone = string.Join(" ", _microphone.OrderBy(record => record.Timestamp).Select(record => record.Text));
			application = string.Join(" ", _application.OrderBy(record => record.Timestamp).Select(record => record.Text));
			_saving = true;
		}
		try
		{
			Directory.CreateDirectory(directory);
			await File.WriteAllTextAsync(Path.Combine(directory, "microphone.txt"), microphone, new UTF8Encoding(false), cancellationToken);
			await File.WriteAllTextAsync(Path.Combine(directory, "application.txt"), application, new UTF8Encoding(false), cancellationToken);
			lock (_sync)
			{
				_microphone.Clear();
				_application.Clear();
				_sessionDirectory = null;
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
}
