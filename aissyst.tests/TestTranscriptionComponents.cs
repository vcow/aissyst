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
	internal readonly TestSessionSelector Selector;
	private readonly TextProcessor _processor;
	public TestTextProcessor(TestSessionSelector? selector = null)
	{
		Selector = selector ?? new TestSessionSelector();
		_processor = new TextProcessor(Selector, new TestSessionDatabase(),
			Microsoft.Extensions.Logging.Abstractions.NullLogger<TextProcessor>.Instance);
	}
	private readonly ConcurrentQueue<DialogMessage> _dialog = new();
	internal Channel<(AudioSourceType Source, string Text)> Results { get; } =
		Channel.CreateUnbounded<(AudioSourceType, string)>();
	public bool IsReady => _processor.IsReady;

	public Task InitializeAsync(CancellationToken cancellationToken = default) => _processor.InitializeAsync(cancellationToken);

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
