using aissyst.console;
using aissyst.core;
using aissyst.core.Audio;
using aissyst.core.Controller;
using Microsoft.Extensions.Logging.Abstractions;
using R3;

namespace aissyst.tests;

public class ApplicationControllerTests
{
	[Fact]
	public async Task BothSourcesAreTranscribedAndDeliveredToTextProcessor()
	{
		using var source = new TestCaptureSession();
		var transcriber = new TestTranscriber { Recognize = (frames, _) => Task.FromResult($"Text {frames[0].Samples.Span[0]}") };
		var processor = new TestTextProcessor();
		await using var controller = Create(source, transcriber, processor);
		await controller.InitializeAsync();
		source.Microphone.OnNext(Frames(1));
		source.Application.OnNext(Frames(2));
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		Assert.Equal((AudioSourceType.Microphone, "Text 1"), await processor.Results.Reader.ReadAsync(timeout.Token));
		Assert.Equal((AudioSourceType.Application, "Text 2"), await processor.Results.Reader.ReadAsync(timeout.Token));
		Assert.Equal(2, transcriber.Requests.Count);
		Assert.Equal(1, transcriber.Initializations);
		Assert.True(source.Initialized);
		Assert.Equal(1, processor.Selector.Calls);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task PendingRecordsCanBeClearedOrDroppedWithoutCancellingActiveTranscription(bool overflow)
	{
		using var source = new TestCaptureSession();
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		CancellationToken activeToken = default;
		var transcriber = new TestTranscriber
		{
			Recognize = async (frames, cancellationToken) =>
			{
				var value = frames[0].Samples.Span[0];
				if (value == 1)
				{
					activeToken = cancellationToken;
					entered.TrySetResult();
					await release.Task.WaitAsync(cancellationToken);
				}
				return $"Text {value}";
			}
		};
		var processor = new TestTextProcessor();
		await using var controller = Create(source, transcriber, processor,
			new ApplicationSettings(transcriptionQueueCapacity: overflow ? 1 : 10));
		await controller.InitializeAsync();
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		source.Microphone.OnNext(Frames(1));
		await entered.Task.WaitAsync(timeout.Token);
		source.Application.OnNext(Frames(2));
		source.Application.OnNext(Frames(3));
		if (!overflow)
		{
			Assert.Equal((2, 0), await controller.ClearQueues(timeout.Token));
			Assert.False(activeToken.IsCancellationRequested);
		}
		source.Application.OnNext(Frames(4));
		release.TrySetResult();
		Assert.Equal((AudioSourceType.Microphone, "Text 1"), await processor.Results.Reader.ReadAsync(timeout.Token));
		Assert.Equal((AudioSourceType.Application, "Text 4"), await processor.Results.Reader.ReadAsync(timeout.Token));
		Assert.Equal(2, transcriber.Requests.Count);
	}

	[Fact]
	public async Task DisposalCancelsActiveTranscriptionAndIsIdempotent()
	{
		using var source = new TestCaptureSession();
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var transcriber = new TestTranscriber
		{
			Recognize = async (_, cancellationToken) =>
			{
				entered.TrySetResult();
				await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
				return string.Empty;
			}
		};
		await using var controller = Create(source, transcriber, new TestTextProcessor());
		await controller.InitializeAsync();
		source.Application.OnNext(Frames(1));
		await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
		await controller.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
		await controller.DisposeAsync();
	}

	[Fact]
	public async Task OutputReceivesOnlyCompletedTurnsAndNoAutomaticLlmRequestsAreMade()
	{
		using var source = new TestCaptureSession();
		var transcriber = new TestTranscriber { Recognize = (frames, _) => Task.FromResult($"Text {frames[0].Samples.Span[0]}") };
		var output = new TestOutput();
		var connector = new TestLLMConnector();
		await using var controller = Create(source, transcriber, new TestTextProcessor(), output: output, connector: connector);
		await controller.InitializeAsync();
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		source.Microphone.OnNext(Frames(1));
		await controller.WaitForIdleAsync(timeout.Token);
		source.Microphone.OnNext(Frames(2));
		await controller.WaitForIdleAsync(timeout.Token);
		Assert.Empty(output.Messages);
		source.Application.OnNext(Frames(3));
		await controller.WaitForIdleAsync(timeout.Token);
		var message = Assert.Single(output.Messages);
		Assert.Equal(AudioSourceType.Microphone, message.Source);
		Assert.Equal("Text 1 Text 2", message.Text);
		source.Application.OnNext(Frames(4));
		await controller.WaitForIdleAsync(timeout.Token);
		Assert.Single(output.Messages);
		Assert.Empty(output.Answers);
		Assert.Equal(0, connector.SessionInitializations);
		Assert.Equal(0, connector.Requests);
	}

	private static ApplicationController Create(TestCaptureSession source, TestTranscriber transcriber,
		TestTextProcessor processor, ApplicationSettings? settings = null, TestOutput? output = null,
		TestLLMConnector? connector = null) =>
		new(transcriber, processor, source, NullLogger<ApplicationController>.Instance, connector ?? new TestLLMConnector(),
			output ?? new TestOutput(), settings ?? new ApplicationSettings(), processor.Selector);

	private static IReadOnlyList<AudioFrame> Frames(float value) =>
		[new(new float[] { value }, new AudioFormat(16000, 1), TimeSpan.Zero)];

	private sealed class TestCaptureSession : ICaptureSession, IDisposable
	{
		internal readonly Subject<IReadOnlyList<AudioFrame>> Microphone = new();
		internal readonly Subject<IReadOnlyList<AudioFrame>> Application = new();
		internal bool Initialized;
		public AudioInputDevice? SelectedMicrophone => null;
		public AudioApplication? SelectedApplication => null;
		public Observable<IReadOnlyList<AudioFrame>> MicrophoneRecordObservable => Microphone;
		public Observable<IReadOnlyList<AudioFrame>> ApplicationRecordingObservable => Application;
		public Task InitializeAsync(CancellationToken cancellationToken = default)
		{
			Initialized = true;
			return Task.CompletedTask;
		}
		public void Dispose()
		{
			Microphone.Dispose();
			Application.Dispose();
		}
	}
}
