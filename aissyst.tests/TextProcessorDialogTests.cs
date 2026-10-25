using aissyst.console;
using aissyst.core;
using aissyst.core.Processor;
using Microsoft.Extensions.Logging.Abstractions;

namespace aissyst.tests;

public sealed class TextProcessorDialogTests : IDisposable
{
	private readonly string _directory = Path.Combine(Path.GetTempPath(), $"aissyst-dialog-tests-{Guid.NewGuid():N}");

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task DialogIsChronologicalAndMergesOnlyConsecutiveNonEmptyMessages(bool useFiles)
	{
		var processor = await CreateAsync(useFiles);
		var start = DateTime.UtcNow;
		processor.Process(new DialogMessage(start.AddSeconds(3), AudioSourceType.Application, "Third."));
		processor.Process(new DialogMessage(start.AddSeconds(1), AudioSourceType.Microphone, "First."));
		processor.Process(new DialogMessage(start.AddMilliseconds(1500), AudioSourceType.Application, " \t "));
		processor.Process(new DialogMessage(start.AddSeconds(2), AudioSourceType.Microphone, "Second."));
		processor.Process(new DialogMessage(start.AddSeconds(4), AudioSourceType.Application, "Fourth."));
		processor.Process(new DialogMessage(start.AddSeconds(5), AudioSourceType.Microphone, "Fifth."));
		Assert.Equal(new[]
		{
			new DialogMessage(start.AddSeconds(2), AudioSourceType.Microphone, "First. Second."),
			new DialogMessage(start.AddSeconds(4), AudioSourceType.Application, "Third. Fourth."),
			new DialogMessage(start.AddSeconds(5), AudioSourceType.Microphone, "Fifth.")
		}, processor.GetDialog(start));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ReadingDialogReturnsStableSnapshotAndOnlyNewFragmentsAfterItsBoundary(bool useFiles)
	{
		var processor = await CreateAsync(useFiles);
		var start = DateTime.UtcNow;
		processor.Process(new DialogMessage(start, AudioSourceType.Application, "Already summarized."));
		Assert.Empty(processor.GetDialog(start));
		processor.Process(new DialogMessage(start.AddSeconds(1), AudioSourceType.Application, "New fragment."));
		var snapshot = processor.GetDialog(start);
		var message = Assert.Single(snapshot);
		Assert.Equal("New fragment.", message.Text);
		processor.Process(new DialogMessage(start.AddSeconds(2), AudioSourceType.Application, "Continuation."));
		Assert.Equal("New fragment.", Assert.Single(snapshot).Text);
		Assert.Equal(new DialogMessage(start.AddSeconds(2), AudioSourceType.Application, "Continuation."),
			Assert.Single(processor.GetDialog(message.Timestamp)));
		Assert.Equal("New fragment. Continuation.", Assert.Single(processor.GetDialog(start)).Text);
	}

	[Fact]
	public async Task ConcurrentLoggingPreservesEveryMessageInHistory()
	{
		var processor = await CreateAsync(false);
		var start = DateTime.UtcNow;
		await Task.WhenAll(Enumerable.Range(1, 100).Select(index => Task.Run(() =>
			processor.Process(new DialogMessage(start.AddTicks(index), AudioSourceType.Application, $"word{index}")))));
		Assert.Equal(string.Join(" ", Enumerable.Range(1, 100).Select(index => $"word{index}")),
			Assert.Single(processor.GetDialog(start)).Text);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ProcessReturnsPreviousTurnOnlyWhenANonEmptyMessageFromAnotherSourceArrives(bool useFiles)
	{
		var processor = await CreateAsync(useFiles);
		var start = DateTime.UtcNow;
		Assert.Null(processor.Process(new DialogMessage(start, AudioSourceType.Application, "First.")));
		Assert.Null(processor.Process(new DialogMessage(start.AddSeconds(1), AudioSourceType.Application, "Second.")));
		Assert.Null(processor.Process(new DialogMessage(start.AddSeconds(2), AudioSourceType.Microphone, " \t ")));
		Assert.Equal(new DialogMessage(start.AddSeconds(1), AudioSourceType.Application, "First. Second."),
			processor.Process(new DialogMessage(start.AddSeconds(3), AudioSourceType.Microphone, "Reply.")));
		Assert.Null(processor.Process(new DialogMessage(start.AddSeconds(4), AudioSourceType.Microphone, "Continuation.")));
		Assert.Equal(new DialogMessage(start.AddSeconds(4), AudioSourceType.Microphone, "Reply. Continuation."),
			processor.Process(new DialogMessage(start.AddSeconds(5), AudioSourceType.Application, "Next.")));
		Assert.Equal("Next.", processor.GetDialog(start.AddSeconds(4)).Single().Text);
	}

	private async Task<ITextProcessor> CreateAsync(bool useFiles)
	{
		var selector = new TestSessionSelector();
		await selector.InitializeAsync();
		ITextProcessor processor = useFiles
			? new SessionFileTextProcessor(_directory, NullLogger<SessionFileTextProcessor>.Instance)
			: new TextProcessor(selector, new TestSessionDatabase(), NullLogger<TextProcessor>.Instance);
		await processor.InitializeAsync();
		if (processor is SessionFileTextProcessor fileProcessor)
		{
			await fileProcessor.BeginSessionAsync();
		}
		Assert.Empty(processor.GetDialog(DateTime.MinValue));
		return processor;
	}

	public void Dispose()
	{
		if (Directory.Exists(_directory))
		{
			Directory.Delete(_directory, recursive: true);
		}
	}
}
