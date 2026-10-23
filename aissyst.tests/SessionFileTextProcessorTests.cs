using aissyst.core;
using aissyst.core.Processor;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace aissyst.tests;

public sealed class SessionFileTextProcessorTests : IDisposable
{
	private readonly string _directory = Path.Combine(Path.GetTempPath(), $"aissyst-transcripts-{Guid.NewGuid():N}");

	[Fact]
	public async Task SessionSavesSeparatePlainTextFilesOnlyAfterCompletionAndLogsText()
	{
		var logger = new TestLogger();
		var processor = new SessionFileTextProcessor(_directory, logger);
		await processor.InitializeAsync();
		await processor.BeginSessionAsync();
		var timestamp = DateTime.UtcNow;
		await processor.ProcessAsync(timestamp.AddSeconds(1), AudioSourceType.Application, "Second\r\n fragment.\t");
		await processor.ProcessAsync(timestamp, AudioSourceType.Microphone, "My\n words.");
		await processor.ProcessAsync(timestamp, AudioSourceType.Application, " First   fragment. ");
		await processor.ProcessAsync(timestamp, AudioSourceType.Application, "Same timestamp.");
		await processor.ProcessAsync(timestamp, AudioSourceType.Microphone, " \t\r\n ");
		Assert.False(Directory.Exists(_directory));
		Assert.Contains(logger.Messages, message => message.Contains("My words."));
		await processor.CompleteSessionAsync();
		var session = Assert.Single(Directory.GetDirectories(_directory));
		Assert.Equal(2, Directory.GetFiles(session).Length);
		Assert.Equal("My words.", await File.ReadAllTextAsync(Path.Combine(session, "microphone.txt")));
		Assert.Equal("First fragment. Same timestamp. Second fragment.",
			await File.ReadAllTextAsync(Path.Combine(session, "application.txt")));
		Assert.Contains(logger.Messages, message => message.Contains(session));
	}

	[Fact]
	public async Task ConsecutiveSessionsDoNotMixOrOverwriteTextAndSilentSourceHasEmptyFile()
	{
		var processor = new SessionFileTextProcessor(_directory, NullLogger<SessionFileTextProcessor>.Instance);
		await processor.InitializeAsync();
		for (var index = 0; index < 2; index++)
		{
			await processor.BeginSessionAsync();
			await processor.ProcessAsync(DateTime.UtcNow, AudioSourceType.Application, $"Session {index}");
			await processor.CompleteSessionAsync();
			await processor.CompleteSessionAsync();
		}
		var sessions = Directory.GetDirectories(_directory);
		Assert.Equal(2, sessions.Length);
		var contents = new List<string>();
		foreach (var session in sessions)
		{
			contents.Add(await File.ReadAllTextAsync(Path.Combine(session, "application.txt")));
			Assert.Empty(await File.ReadAllTextAsync(Path.Combine(session, "microphone.txt")));
		}
		Assert.Equal(new[] { "Session 0", "Session 1" }, contents.Order());
	}

	[Fact]
	public async Task ConcurrentFragmentsAreAllRetainedInTimestampOrder()
	{
		var processor = new SessionFileTextProcessor(_directory, NullLogger<SessionFileTextProcessor>.Instance);
		await processor.InitializeAsync();
		await processor.BeginSessionAsync();
		var start = DateTime.UtcNow;
		await Task.WhenAll(Enumerable.Range(0, 100).Select(index => Task.Run(() =>
			processor.ProcessAsync(start.AddTicks(index), AudioSourceType.Application, $"word{index}"))));
		await processor.CompleteSessionAsync();
		var session = Assert.Single(Directory.GetDirectories(_directory));
		Assert.Equal(string.Join(" ", Enumerable.Range(0, 100).Select(index => $"word{index}")),
			await File.ReadAllTextAsync(Path.Combine(session, "application.txt")));
	}

	public void Dispose()
	{
		if (Directory.Exists(_directory))
		{
			Directory.Delete(_directory, recursive: true);
		}
	}

	private sealed class TestLogger : ILogger<SessionFileTextProcessor>
	{
		internal readonly List<string> Messages = [];
		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
		public bool IsEnabled(LogLevel logLevel) => true;
		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
			Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
	}
}
