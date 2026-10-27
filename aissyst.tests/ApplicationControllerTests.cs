using aissyst.console;
using aissyst.core;
using aissyst.core.Audio;
using aissyst.core.Controller;
using aissyst.core.LLMConnector.DTO;
using aissyst.core.LLMConnector.Model;
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

	[Fact]
	public async Task NewAssistanceCancelsPreviousRequestAndIgnoresItsLateResponse()
	{
		using var source = new TestCaptureSession();
		var processor = new TestTextProcessor();
		var connector = new TestLLMConnector { Initialize = (_, _) => Task.FromResult(new DialogResponse()) };
		var entered = new TaskCompletionSource<DialogRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
		var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource<DialogResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
		connector.Send = async (request, cancellationToken) =>
		{
			if (request.Request == RequestType.Translate)
			{
				using var registration = cancellationToken.Register(() => cancelled.TrySetResult());
				entered.TrySetResult(request);
				// Simulate a provider that finishes after cancellation was requested.
				return await release.Task;
			}
			return new DialogResponse { RequestId = request.RequestId, Summary = "Latest summary.", Message = "Latest answer." };
		};
		var output = new TestOutput();
		await using var model = new SessionModel(processor.Selector, new TestSessionDatabase(), processor,
			connector, new ApplicationSettings(), NullLogger<SessionModel>.Instance);
		await using var controller = Create(source, new TestTranscriber(), processor, connector: connector, output: output, model: model);
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		try
		{
			await controller.InitializeAsync(timeout.Token);
			var first = controller.SendRequestAsync(RequestType.Translate, timeout.Token);
			var payload = await entered.Task.WaitAsync(timeout.Token);
			await controller.WaitForIdleAsync(timeout.Token);
			Assert.False(first.IsCompleted);
			await controller.SendRequestAsync(RequestType.Reply, timeout.Token);
			await cancelled.Task.WaitAsync(timeout.Token);
			release.TrySetResult(new DialogResponse { RequestId = payload.RequestId, Summary = "Obsolete summary.", Message = "Obsolete answer." });
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
			Assert.Equal("Latest answer.", Assert.Single(output.Answers));
			Assert.Equal("Latest summary.", model.GetRequest(RequestType.Summary).Summary);
		}
		finally
		{
			release.TrySetResult(new DialogResponse());
		}
	}

	private static ApplicationController Create(TestCaptureSession source, TestTranscriber transcriber,
		TestTextProcessor processor, ApplicationSettings? settings = null, TestOutput? output = null,
		TestLLMConnector? connector = null, SessionModel? model = null) =>
		new(transcriber, processor, source, NullLogger<ApplicationController>.Instance, connector ?? new TestLLMConnector(),
			output ?? new TestOutput(), settings ?? new ApplicationSettings(), processor.Selector, model);

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
