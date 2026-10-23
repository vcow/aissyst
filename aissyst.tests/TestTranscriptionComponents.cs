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
	internal Channel<(AudioSourceType Source, string Text)> Results { get; } =
		Channel.CreateUnbounded<(AudioSourceType, string)>();
	public bool IsReady { get; private set; }

	public Task InitializeAsync(CancellationToken cancellationToken = default)
	{
		IsReady = true;
		return Task.CompletedTask;
	}

	public Task ProcessAsync(DateTime timestamp, AudioSourceType source, string text,
		CancellationToken cancellationToken = default)
	{
		Assert.True(IsReady);
		Results.Writer.TryWrite((source, text));
		return Task.CompletedTask;
	}
}
