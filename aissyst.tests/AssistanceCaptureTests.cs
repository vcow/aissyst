using aissyst.console;
using aissyst.core;
using aissyst.core.Audio;
using aissyst.core.Controller;
using aissyst.core.LLMConnector.DTO;
using aissyst.core.LLMConnector.Model;
using aissyst.core.Processor;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace aissyst.tests;

public sealed class AssistanceCaptureTests
{
	private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

	[Fact]
	public async Task AssistanceWaitsForTranscriptionAndPersistenceRejectsRepeatsAndResumesBeforeLlmReply()
	{
		await using var fixture = new Fixture();
		await fixture.InitializeAsync();
		var transcribing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseTranscription = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var saving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var sent = new TaskCompletionSource<DialogRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		fixture.Transcriber.Recognize = async (frames, token) =>
		{
			if (frames[0].Samples.Span[0] == 0.5f)
			{
				transcribing.TrySetResult();
				await releaseTranscription.Task.WaitAsync(token);
				return "First question";
			}
			return "Later question";
		};
		fixture.Database.BeforeAppend = async (message, token) =>
		{
			if (message.Text == "First question")
			{
				saving.TrySetResult();
				await releaseSave.Task.WaitAsync(token);
			}
		};
		fixture.Connector.Send = async (request, token) =>
		{
			sent.TrySetResult(request);
			await releaseReply.Task.WaitAsync(token);
			return Answer(request);
		};
		try
		{
			await fixture.Capture.EmitApplicationAsync(Frame(0.5f));
			var request = fixture.Controller.SendRequestAsync(RequestType.Translate, pauseAudioProcessing: fixture.Session.PauseAndCompleteRecordingsAsync);
			await transcribing.Task.WaitAsync(Timeout);
			Assert.Equal(0, fixture.Connector.Requests);
			var repeatedPreparation = false;
			await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Controller.SendRequestAsync(RequestType.Reply,
				pauseAudioProcessing: token => { repeatedPreparation = true; return fixture.Session.PauseAndCompleteRecordingsAsync(token); }));
			Assert.False(repeatedPreparation);
			await fixture.Capture.EmitApplicationAsync(Frame(0.25f));
			var laterFragment = fixture.Session.CompleteRecordingsAsync();
			Assert.False(laterFragment.IsCompleted);
			releaseTranscription.TrySetResult();
			await saving.Task.WaitAsync(Timeout);
			Assert.Equal(0, fixture.Connector.Requests);
			releaseSave.TrySetResult();
			var payload = await sent.Task.WaitAsync(Timeout);
			Assert.Equal("First question", Assert.Single(payload.RawRecords).Message);
			await laterFragment.WaitAsync(Timeout);
			await fixture.Controller.WaitForIdleAsync().WaitAsync(Timeout);
			Assert.Equal(2, fixture.Transcriber.Requests.Count);
			Assert.Contains("Later question", Assert.Single(fixture.Processor.GetDialog(DateTime.MinValue)).Text);
			Assert.Equal("First question", Assert.Single(payload.RawRecords).Message);
			Assert.False(request.IsCompleted);
			Assert.Empty(fixture.Output.Answers);
			releaseReply.TrySetResult();
			Assert.NotNull(await request.WaitAsync(Timeout));
			Assert.Single(fixture.Output.Answers);
		}
		finally
		{
			releaseTranscription.TrySetResult();
			releaseSave.TrySetResult();
			releaseReply.TrySetResult();
		}
	}

	[Fact]
	public async Task SilenceDoesNotWaitForNewTextAndCaptureContinues()
	{
		await using var fixture = new Fixture();
		await fixture.InitializeAsync();
		fixture.Connector.Send = (request, _) => Task.FromResult(Answer(request));
		await fixture.Controller.SendRequestAsync(RequestType.Translate,
			pauseAudioProcessing: fixture.Session.PauseAndCompleteRecordingsAsync).WaitAsync(Timeout);
		Assert.Empty(fixture.Transcriber.Requests);
		await fixture.Capture.EmitApplicationAsync(Frame(0.5f));
		await fixture.Session.CompleteRecordingsAsync().WaitAsync(Timeout);
		await fixture.Controller.WaitForIdleAsync().WaitAsync(Timeout);
		Assert.Single(fixture.Transcriber.Requests);
	}

	[Fact]
	public async Task CancellationDuringTranscriptionResumesCaptureAndReleasesTheAssistanceGuard()
	{
		await using var fixture = new Fixture();
		await fixture.InitializeAsync();
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		fixture.Transcriber.Recognize = async (_, token) =>
		{
			entered.TrySetResult();
			await release.Task.WaitAsync(token);
			return "Question";
		};
		using var cancellation = new CancellationTokenSource();
		try
		{
			await fixture.Capture.EmitApplicationAsync(Frame(0.5f));
			var request = fixture.Controller.SendRequestAsync(RequestType.Translate, cancellation.Token, fixture.Session.PauseAndCompleteRecordingsAsync);
			await entered.Task.WaitAsync(Timeout);
			await cancellation.CancelAsync();
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(Timeout));
			Assert.Equal(0, fixture.Connector.Requests);
			await fixture.Capture.EmitApplicationAsync(Frame(0.25f));
			await fixture.Session.CompleteRecordingsAsync().WaitAsync(Timeout);
			release.TrySetResult();
			await fixture.Controller.WaitForIdleAsync().WaitAsync(Timeout);
			fixture.Connector.Send = (payload, _) => Task.FromResult(Answer(payload));
			Assert.NotNull(await fixture.Controller.SendRequestAsync(RequestType.Reply,
				pauseAudioProcessing: fixture.Session.PauseAndCompleteRecordingsAsync).WaitAsync(Timeout));
		}
		finally
		{
			release.TrySetResult();
		}
	}

	[Fact]
	public async Task SnapshotFailureResumesCaptureAndAllowsAnotherAssistanceRequest()
	{
		await using var fixture = new Fixture();
		await fixture.InitializeAsync();
		await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Controller.SendRequestAsync((RequestType)(-1),
			pauseAudioProcessing: fixture.Session.PauseAndCompleteRecordingsAsync).WaitAsync(Timeout));
		await fixture.Capture.EmitApplicationAsync(Frame(0.5f));
		await fixture.Session.CompleteRecordingsAsync().WaitAsync(Timeout);
		await fixture.Controller.WaitForIdleAsync().WaitAsync(Timeout);
		fixture.Connector.Send = (request, _) => Task.FromResult(Answer(request));
		Assert.NotNull(await fixture.Controller.SendRequestAsync(RequestType.Translate,
			pauseAudioProcessing: fixture.Session.PauseAndCompleteRecordingsAsync).WaitAsync(Timeout));
	}

	private static DialogResponse Answer(DialogRequest request) => new()
	{
		RequestId = request.RequestId, Summary = request.RawRecords.Count == 0 ? request.Summary : "Updated summary",
		SummaryTimestamp = request.RawRecords.LastOrDefault()?.Timestamp, Message = "Answer"
	};

	private static AudioFrame Frame(float value) => new(Enumerable.Repeat(value, 320).ToArray(), new AudioFormat(16000, 1), TimeSpan.Zero);

	private sealed class Fixture : IAsyncDisposable
	{
		private readonly IHost _host;
		private readonly AsyncServiceScope _scope;
		internal readonly TestAudioCapture Capture = new();
		internal readonly TestTranscriber Transcriber = new();
		internal readonly TestSessionDatabase Database = new();
		internal readonly TestLLMConnector Connector = new() { Initialize = (_, _) => Task.FromResult(new DialogResponse()) };
		internal readonly TestOutput Output = new();
		internal readonly TextProcessor Processor;
		internal readonly SessionModel Model;
		internal readonly ApplicationController Controller;
		internal readonly CaptureSession Session;
		private readonly TestSessionSelector _selector = new();

		internal Fixture()
		{
			var settings = new ApplicationSettings(TimeSpan.FromMinutes(1));
			var builder = Host.CreateApplicationBuilder();
			builder.Logging.ClearProviders();
			builder.Services.AddSingleton<IAudioCapture>(Capture);
			builder.Services.AddSingleton<IApplicationSettings>(settings);
			builder.Services.AddSingleton(new ConsoleHotkeySettings());
			builder.Services.AddSingleton<ConsoleCaptureSession>();
			builder.Services.AddScoped<CaptureSessionContext>();
			builder.Services.AddScoped<CaptureSession>();
			_host = builder.Build();
			_scope = _host.Services.CreateAsyncScope();
			_scope.ServiceProvider.GetRequiredService<CaptureSessionContext>().Initialize(null, new AudioApplication(123, "Test application", false, [123]));
			Session = _scope.ServiceProvider.GetRequiredService<CaptureSession>();
			Processor = new TextProcessor(_selector, Database, NullLogger<TextProcessor>.Instance);
			var dialog = new DialogCoordinator(Processor);
			Model = new SessionModel(_selector, Database, dialog, Connector, settings, NullLogger<SessionModel>.Instance);
			Controller = new ApplicationController(Transcriber, dialog, _host.Services.GetRequiredService<ConsoleCaptureSession>(),
				NullLogger<ApplicationController>.Instance, Connector, Output, settings, _selector, Model);
		}

		internal async Task InitializeAsync()
		{
			await _selector.InitializeAsync();
			await Transcriber.InitializeAsync();
			await Model.InitializeAsync();
			Session.Start();
		}

		public async ValueTask DisposeAsync()
		{
			await _scope.DisposeAsync();
			await Controller.DisposeAsync();
			await Model.DisposeAsync();
			await Processor.DisposeAsync();
			_host.Dispose();
		}
	}
}
