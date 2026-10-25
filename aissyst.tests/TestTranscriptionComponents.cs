using System.Collections.Concurrent;
using System.Threading.Channels;
using aissyst.core;
using aissyst.core.Audio;
using aissyst.core.Processor;
using aissyst.core.Transcription;

namespace aissyst.tests;

internal sealed class TestTranscriber : ITranscriber
{
	internal ConcurrentQueue<IReadOnlyList<AudioFrame>> Requests { get; } = new();
	internal Func<IReadOnlyList<AudioFrame>, CancellationToken, Task<string>> Recognize { get; set; } =
		(_, _) => Task.FromResult("Recognized text");
	internal Exception? InitializationError { get; set; }
	internal int Initializations { get; private set; }
	public bool IsReady { get; private set; }

	public Task InitializeAsync(CancellationToken cancellationToken = default)
	{
		Initializations++;
		if (InitializationError != null)
		{
			return Task.FromException(InitializationError);
		}
		IsReady = true;
		return Task.CompletedTask;
	}

	public Task<string> TranscribeAsync(IReadOnlyList<AudioFrame> frames, CancellationToken cancellationToken = default)
	{
		Requests.Enqueue(frames);
		return Recognize(frames, cancellationToken);
	}

	public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class TestTextProcessor : ITextProcessor
{
	private readonly LoggingTextProcessor _processor = new(Microsoft.Extensions.Logging.Abstractions.NullLogger<LoggingTextProcessor>.Instance);
	private readonly ConcurrentQueue<DialogMessage> _dialog = new();
	internal Channel<(AudioSourceType Source, string Text)> Results { get; } =
		Channel.CreateUnbounded<(AudioSourceType, string)>();
	public bool IsReady { get; private set; }

	public Task InitializeAsync(CancellationToken cancellationToken = default)
	{
		IsReady = true;
		return _processor.InitializeAsync(cancellationToken);
	}

	public DialogMessage? Process(DialogMessage message)
	{
		Assert.True(IsReady);
		_dialog.Enqueue(message);
		Results.Writer.TryWrite((message.Source, message.Text));
		return _processor.Process(message);
	}

	public IReadOnlyList<DialogMessage> GetDialog(DateTime fromTime)
	{
		return _dialog.ToArray().ToDialog(fromTime);
	}
}
