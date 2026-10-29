using aissyst.console;
using aissyst.core;
using aissyst.core.Audio;
using aissyst.core.Controller;
using aissyst.core.Processor;
using aissyst.core.Database;
using aissyst.core.LLMConnector;
using aissyst.core.LLMConnector.DTO;
using aissyst.core.LLMConnector.Model;
using Microsoft.Extensions.Logging.Abstractions;

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
		source.PublishMicrophone(Frames(1));
		source.PublishApplication(Frames(2));
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
		source.PublishMicrophone(Frames(1));
		await entered.Task.WaitAsync(timeout.Token);
		source.PublishApplication(Frames(2));
		source.PublishApplication(Frames(3));
		if (!overflow)
		{
			Assert.Equal((2, 0), await controller.ClearQueues(timeout.Token));
			Assert.False(activeToken.IsCancellationRequested);
		}
		source.PublishApplication(Frames(4));
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
		source.PublishApplication(Frames(1));
		await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
		await controller.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
		await controller.DisposeAsync();
		Assert.False(source.HasSubscribers);
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
		source.PublishMicrophone(Frames(1));
		await controller.WaitForIdleAsync(timeout.Token);
		source.PublishMicrophone(Frames(2));
		await controller.WaitForIdleAsync(timeout.Token);
		Assert.Empty(output.Messages);
		source.PublishApplication(Frames(3));
		await controller.WaitForIdleAsync(timeout.Token);
		var message = Assert.Single(output.Messages);
		Assert.Equal(AudioSourceType.Microphone, message.Source);
		Assert.Equal("Text 1 Text 2", message.Text);
		source.PublishApplication(Frames(4));
		await controller.WaitForIdleAsync(timeout.Token);
		Assert.Single(output.Messages);
		Assert.Empty(output.Answers);
		Assert.Equal(0, connector.SessionInitializations);
		Assert.Equal(0, connector.Requests);
	}

	[Fact]
	public async Task BusyAssistanceIsRejectedWithoutCancelingActiveRequest()
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
			return new DialogResponse { RequestId = request.RequestId, Summary = request.Summary, Message = "Latest answer." };
		};
		var output = new TestOutput();
		var dialog = new DialogCoordinator(processor);
		await using var model = new SessionModel(processor.Selector, new TestSessionDatabase(), dialog,
			connector, new ApplicationSettings(), NullLogger<SessionModel>.Instance);
		await using var controller = Create(source, new TestTranscriber(), processor, connector: connector, output: output, model: model, dialog: dialog);
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		try
		{
			await controller.InitializeAsync(timeout.Token);
			var first = controller.SendRequestAsync(RequestType.Translate, timeout.Token);
			var payload = await entered.Task.WaitAsync(timeout.Token);
			await controller.WaitForIdleAsync(timeout.Token);
			Assert.False(first.IsCompleted);
			await Assert.ThrowsAsync<InvalidOperationException>(() => controller.SendRequestAsync(RequestType.Reply, timeout.Token));
			Assert.False(cancelled.Task.IsCompleted);
			Assert.Equal(1, connector.Requests);
			release.TrySetResult(new DialogResponse { RequestId = payload.RequestId, Summary = payload.Summary, Message = "First answer." });
			await first;
			await controller.SendRequestAsync(RequestType.Reply, timeout.Token);
			Assert.Equal(new[] { "First answer.", "Latest answer." }, output.Answers);
		}
		finally
		{
			release.TrySetResult(new DialogResponse());
		}
	}

	[Fact]
	public async Task WaitForIdleIncludesSavingOriginalTranscription()
	{
		using var source = new TestCaptureSession();
		var selector = new TestSessionSelector();
		var database = new TestSessionDatabase();
		await using var processor = new TextProcessor(selector, database, NullLogger<TextProcessor>.Instance);
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		database.BeforeAppend = async (_, token) => { entered.SetResult(); await release.Task.WaitAsync(token); };
		var transcriber = new TestTranscriber { Recognize = (_, _) => Task.FromResult("  Original\nfragment  ") };
		await using var controller = new ApplicationController(transcriber, new DialogCoordinator(processor), source,
			NullLogger<ApplicationController>.Instance, new TestLLMConnector(), new TestOutput(),
			new ApplicationSettings(), selector);
		await controller.InitializeAsync();
		source.PublishMicrophone(Frames(1));
		await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
		var idle = controller.WaitForIdleAsync();
		Assert.False(idle.IsCompleted);
		release.SetResult();
		await idle.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Equal("  Original\nfragment  ", Assert.Single(database.Session.Dialog).Message);
		Assert.Equal("Original\nfragment", Assert.Single(processor.GetDialog(DateTime.MinValue)).Text);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task RecoveryRedrawsStoredAndPendingDialogWithoutDuplicates(bool failedSave)
	{
		using var source = new TestCaptureSession();
		var selector = new TestSessionSelector();
		var database = new TestSessionDatabase();
		database.Session = database.Session with { Dialog = [new DialogRecord(DateTime.UtcNow.AddMinutes(-1), "interviewer", "Stored")] };
		await using var processor = new TextProcessor(selector, database, NullLogger<TextProcessor>.Instance);
		var connector = new TestLLMConnector
		{
			Initialize = (session, _) => Task.FromResult(new DialogResponse
			{
				Summary = "Initial", SummaryTimestamp = session.Dialog.LastOrDefault()?.Timestamp
			})
		};
		var dialog = new DialogCoordinator(processor);
		var output = new TestOutput();
		await using var model = new SessionModel(selector, database, dialog, connector,
			new ApplicationSettings(), NullLogger<SessionModel>.Instance, new SessionRecoveryOptions { Delay = TimeSpan.Zero });
		await using var controller = new ApplicationController(new TestTranscriber { Recognize = (frames, _) => Task.FromResult($"Text {frames[0].Samples.Span[0]}") },
			dialog, source, NullLogger<ApplicationController>.Instance, connector, output, new ApplicationSettings(), selector, model);
		await controller.InitializeAsync();
		Assert.Equal("Stored", Assert.Single(output.Messages).Text);
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		database.BeforeAppend = async (_, token) =>
		{
			entered.TrySetResult();
			await release.Task.WaitAsync(token);
			if (failedSave)
			{
				throw new IOException("Offline");
			}
		};
		source.PublishMicrophone(Frames(1));
		await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
		connector.Disconnect();
		Assert.False(model.IsReady);
		release.SetResult();
		await SessionModelTests.WaitUntilAsync(() => model.IsReady && connector.Reconnects == 1);
		var restored = model.GetSession();
		await controller.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Equal(new[] { "Stored", "Text 1" }, output.Messages.Select(message => message.Text));
		Assert.Equal(2, output.ClearCalls);
		Assert.Equal(2, output.BatchCalls);
		Assert.Equal(2, restored.Dialog.Count);
		if (failedSave)
		{
			Assert.Single(database.Session.Dialog);
		}
		database.BeforeAppend = null;
		source.PublishApplication(Frames(2));
		await controller.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
		source.PublishMicrophone(Frames(3));
		await controller.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Equal(new[] { "Stored", "Text 1", "Text 2" }, output.Messages.Select(message => message.Text));
	}

	private static ApplicationController Create(TestCaptureSession source, TestTranscriber transcriber,
		TestTextProcessor processor, ApplicationSettings? settings = null, TestOutput? output = null,
		TestLLMConnector? connector = null, SessionModel? model = null, DialogCoordinator? dialog = null) =>
		new(transcriber, dialog ?? new DialogCoordinator(processor), source, NullLogger<ApplicationController>.Instance, connector ?? new TestLLMConnector(),
			output ?? new TestOutput(), settings ?? new ApplicationSettings(), processor.Selector, model);

	private static IReadOnlyList<AudioFrame> Frames(float value) =>
		[new(new float[] { value }, new AudioFormat(16000, 1), TimeSpan.Zero)];

	private sealed class TestCaptureSession : ICaptureSession, IDisposable
	{
		internal bool Initialized;
		public AudioInputDevice? SelectedMicrophone => null;
		public AudioApplication? SelectedApplication => null;
		public event Action<IReadOnlyList<AudioFrame>>? MicrophoneRecordingCompleted;
		public event Action<IReadOnlyList<AudioFrame>>? ApplicationRecordingCompleted;
		public bool HasSubscribers => MicrophoneRecordingCompleted != null || ApplicationRecordingCompleted != null;
		public void PublishMicrophone(IReadOnlyList<AudioFrame> frames) => MicrophoneRecordingCompleted?.Invoke(frames);
		public void PublishApplication(IReadOnlyList<AudioFrame> frames) => ApplicationRecordingCompleted?.Invoke(frames);
		public Task InitializeAsync(CancellationToken cancellationToken = default)
		{
			Initialized = true;
			return Task.CompletedTask;
		}
		public void Dispose()
		{
			MicrophoneRecordingCompleted = null;
			ApplicationRecordingCompleted = null;
		}
	}
}
