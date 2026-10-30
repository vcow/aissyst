using System.Collections.Concurrent;
using System.Threading.Channels;
using aissyst.console;
using aissyst.core;
using aissyst.core.Audio;
using aissyst.core.Controller;
using aissyst.core.Database;
using aissyst.core.LLMConnector.DTO;
using aissyst.core.Output;
using aissyst.core.LLMConnector;
using aissyst.core.LLMConnector.Model;
using aissyst.core.Processor;
using aissyst.core.Transcription;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace aissyst.tests;

[CollectionDefinition("Console workflow", DisableParallelization = true)]
public class ConsoleWorkflowCollection;

[Collection("Console workflow")]
public class AvailableSourcesTests
{
	[Theory]
	[InlineData(AudioSourceType.None, "", 0, 0)]
	[InlineData(AudioSourceType.Microphone, "1\nq\n", 1, 0)]
	[InlineData(AudioSourceType.Application, "1\nq\n", 0, 1)]
	[InlineData(AudioSourceType.Both, "1\n1\nq\n", 1, 1)]
	public async Task StartupRequestsOnlyEnabledSources(AudioSourceType sources, string input,
		int microphoneSelections, int applicationSelections)
	{
		using var host = CreateHost(new ApplicationSettings(availableSources: sources));
		var transcriber = host.Services.GetRequiredService<TestTranscriber>();
		var capture = host.Services.GetRequiredService<TestAudioCapture>();
		capture.EnumeratingSources = () => Assert.True(transcriber.IsReady);
		var output = await RunWorkflowAsync(host, input);
		Assert.Equal(microphoneSelections, capture.MicrophoneEnumerations);
		Assert.Equal(applicationSelections, capture.ApplicationEnumerations);
		Assert.Equal(microphoneSelections != 0, output.Contains("Select microphone by number"));
		Assert.Equal(applicationSelections != 0, output.Contains("Select application by number"));
		Assert.True(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
		Assert.Equal(sources == AudioSourceType.None ? 0 : 1, transcriber.Initializations);
		if (sources == AudioSourceType.None)
		{
			Assert.Contains("No audio sources are enabled", output);
			Assert.DoesNotContain("start a session", output);
		}
	}

	[Fact]
	public async Task ChoosingNoneForBothSourcesExitsWithoutStartingSession()
	{
		using var host = CreateHost(new ApplicationSettings(availableSources: AudioSourceType.Both));
		var output = await RunWorkflowAsync(host, "0\n0\n");
		var capture = host.Services.GetRequiredService<TestAudioCapture>();
		var selection = host.Services.GetRequiredService<ConsoleCaptureSession>();
		Assert.Equal(1, capture.MicrophoneEnumerations);
		Assert.Equal(1, capture.ApplicationEnumerations);
		Assert.Equal(0, capture.MicrophoneCaptures);
		Assert.Equal(0, capture.ApplicationCaptures);
		Assert.False(selection.HasSelectedSources);
		Assert.Contains("0. None", output);
		Assert.Contains("No audio sources selected", output);
		Assert.DoesNotContain("Press Enter to start a session", output);
	}

	[Theory]
	[InlineData("0\n1\n\ns\nq\n", 0, 1)]
	[InlineData("1\n0\n\ns\nq\n", 1, 0)]
	public async Task ChoosingNoneDisablesOnlyThatSource(string input, int microphones, int applications)
	{
		using var host = CreateHost(new ApplicationSettings(availableSources: AudioSourceType.Both));
		var output = await RunWorkflowAsync(host, input);
		var capture = host.Services.GetRequiredService<TestAudioCapture>();
		Assert.Equal(microphones, capture.MicrophoneCaptures);
		Assert.Equal(applications, capture.ApplicationCaptures);
		Assert.Contains("Session started", output);
		Assert.DoesNotContain("No audio sources selected", output);
	}

	[Theory]
	[InlineData(AudioSourceType.Microphone)]
	[InlineData(AudioSourceType.Application)]
	public async Task ChoosingNoneForTheOnlyEnabledSourceExits(AudioSourceType sources)
	{
		using var host = CreateHost(new ApplicationSettings(availableSources: sources));
		var output = await RunWorkflowAsync(host, "0\n");
		Assert.Contains("No audio sources selected", output);
		Assert.DoesNotContain("Session started", output);
	}

	[Fact]
	public async Task EmptyMicrophoneListStillAllowsChoosingApplication()
	{
		using var host = CreateHost(new ApplicationSettings(availableSources: AudioSourceType.Both));
		var capture = host.Services.GetRequiredService<TestAudioCapture>();
		capture.InputDevices = [];
		var output = await RunWorkflowAsync(host, "0\n1\n\ns\nq\n");
		Assert.Contains("No available microphones found", output);
		Assert.Contains("0. None", output);
		Assert.Equal(0, capture.MicrophoneCaptures);
		Assert.Equal(1, capture.ApplicationCaptures);
	}

	[Fact]
	public async Task QuitDuringSourceSelectionDoesNotContinueToNextSource()
	{
		using var host = CreateHost(new ApplicationSettings(availableSources: AudioSourceType.Both));
		var output = await RunWorkflowAsync(host, "q\n");
		Assert.Equal(0, host.Services.GetRequiredService<TestAudioCapture>().ApplicationEnumerations);
		Assert.DoesNotContain("Session started", output);
	}

	[Fact]
	public async Task InitializationFailureStopsBeforeSourceSelection()
	{
		using var host = CreateHost(new ApplicationSettings());
		host.Services.GetRequiredService<TestTranscriber>().InitializationError = new InvalidOperationException("Model load failed.");
		var previousExitCode = Environment.ExitCode;
		try
		{
			await RunWorkflowAsync(host, "");
			Assert.Equal(1, Environment.ExitCode);
			var capture = host.Services.GetRequiredService<TestAudioCapture>();
			Assert.Equal(0, capture.MicrophoneEnumerations);
			Assert.Equal(0, capture.ApplicationEnumerations);
		}
		finally
		{
			Environment.ExitCode = previousExitCode;
		}
	}

	[Theory]
	[InlineData(AudioSourceType.Microphone, 1, 0)]
	[InlineData(AudioSourceType.Application, 0, 1)]
	[InlineData(AudioSourceType.Both, 1, 1)]
	public async Task SessionOpensOnlyEnabledSources(AudioSourceType sources, int microphones, int applications)
	{
		using var host = CreateHost(new ApplicationSettings(availableSources: sources));
		await using var scope = host.Services.CreateAsyncScope();
		scope.ServiceProvider.GetRequiredService<CaptureSessionContext>().Initialize(
			microphones == 1 ? new AudioInputDevice("mic", "Microphone", 1, true) : null,
			applications == 1 ? new AudioApplication(123, "Application", true, [123]) : null);
		var session = scope.ServiceProvider.GetRequiredService<CaptureSession>();
		session.Start();
		await session.CompleteRecordingsAsync().WaitAsync(TimeSpan.FromSeconds(5));
		var capture = host.Services.GetRequiredService<TestAudioCapture>();
		Assert.Equal(microphones, capture.MicrophoneCaptures);
		Assert.Equal(applications, capture.ApplicationCaptures);
		Assert.False(session.Completion.IsCompleted);
	}

	[Fact]
	public async Task CustomHotkeysClearBothQueuesAndRestartCaptureWithoutReinitializingModel()
	{
		using var host = CreateHost(new ApplicationSettings(availableSources: AudioSourceType.Microphone),
			new ConsoleHotkeySettings(finishRecording: 'F', stopSession: 'T', clearQueue: 'X', quit: 'Z'));
		var output = await RunWorkflowAsync(host, "1\n\nx\nt\nx\n\nt\nz\n");
		Assert.Equal(2, host.Services.GetRequiredService<TestAudioCapture>().MicrophoneCaptures);
		Assert.Equal(1, host.Services.GetRequiredService<TestTranscriber>().Initializations);
		Assert.Contains("Removed 0 pending audio recording(s) and 0 pending transcription(s)", output);
		Assert.Contains("F to transcribe and show the current dialog", output);
		Assert.Contains("X to clear pending audio and text", output);
		Assert.DoesNotContain("Unknown command", output);
	}

	[Theory]
	[InlineData(' ')]
	[InlineData('R')]
	public async Task ConfiguredFinishHotkeySubmitsConsecutiveFragmentsWithoutStoppingOrClearingPendingAudio(char finishRecording)
	{
		using var host = CreateHost(new ApplicationSettings(pauseUntilRecordingStops: TimeSpan.FromMinutes(1),
			availableSources: AudioSourceType.Microphone), new ConsoleHotkeySettings(finishRecording: finishRecording));
		var transcriber = host.Services.GetRequiredService<TestTranscriber>();
		var capture = host.Services.GetRequiredService<TestAudioCapture>();
		var processor = (TestTextProcessor)host.Services.GetRequiredService<ITextProcessor>();
		var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		transcriber.Recognize = async (frames, cancellationToken) =>
		{
			var value = frames[0].Samples.Span[0];
			if (value == 0.5f)
			{
				firstStarted.TrySetResult();
				await releaseFirst.Task.WaitAsync(cancellationToken);
			}
			return value == 0.5f ? "First fragment" : "Second fragment";
		};
		var records = new ConcurrentQueue<IReadOnlyList<AudioFrame>>();
		var captureSession = host.Services.GetRequiredService<ConsoleCaptureSession>();
		captureSession.MicrophoneRecordingCompleted += records.Enqueue;
		var previousInput = Console.In;
		var previousOutput = Console.Out;
		using var input = new ControlledInput();
		using var output = new StringWriter();
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		try
		{
			Console.SetIn(input);
			Console.SetOut(output);
			var worker = host.Services.GetRequiredService<MainThreadWorker>();
			await worker.StartAsync(timeout.Token);
			input.Send("1");
			input.Send("");
			while (capture.MicrophoneCaptures == 0)
			{
				await Task.Delay(10, timeout.Token);
			}
			await capture.EmitMicrophoneAsync(new AudioFrame(Enumerable.Repeat(0.5f, 320).ToArray(),
				new AudioFormat(16000, 1), TimeSpan.Zero));
			input.Send(finishRecording.ToString());
			await firstStarted.Task.WaitAsync(timeout.Token);
			await capture.EmitMicrophoneAsync(new AudioFrame(Enumerable.Repeat(0.25f, 320).ToArray(),
				new AudioFormat(16000, 1), TimeSpan.Zero));
			input.Send(finishRecording.ToString());
			while (input.Reads < 5)
			{
				await Task.Delay(10, timeout.Token);
			}
			Assert.Single(records);
			Assert.Single(transcriber.Requests);
			Assert.False(worker.ExecuteTask!.IsCompleted);
			releaseFirst.TrySetResult();
			Assert.Equal((AudioSourceType.Microphone, "First fragment"), await processor.Results.Reader.ReadAsync(timeout.Token));
			Assert.Equal((AudioSourceType.Microphone, "Second fragment"), await processor.Results.Reader.ReadAsync(timeout.Token));
			var dialogOutput = (TestOutput)host.Services.GetRequiredService<IOutput>();
			await SessionModelTests.WaitUntilAsync(() => dialogOutput.Messages.Count == 2);
			Assert.Equal(new[] { "First fragment", "Second fragment" }, dialogOutput.Messages.Select(message => message.Text));
			Assert.Equal(0, host.Services.GetRequiredService<TestLLMConnector>().Requests);
			Assert.Equal(1, capture.MicrophoneCaptures);
			input.Send(finishRecording.ToString()); // No active recording: nothing new should be submitted.
			input.Send("q");
			await worker.ExecuteTask.WaitAsync(timeout.Token);
			Assert.Equal(2, records.Count);
			Assert.Equal(2, transcriber.Requests.Count);
			Assert.DoesNotContain("Queues cleared", output.ToString());
			Assert.DoesNotContain("Unknown command", output.ToString());
		}
		finally
		{
			captureSession.MicrophoneRecordingCompleted -= records.Enqueue;
			input.Complete();
			Console.SetIn(previousInput);
			Console.SetOut(previousOutput);
		}
	}

	[Theory]
	[InlineData(' ', 'R', "Space")]
	[InlineData('R', ' ', "R")]
	public async Task StartupHelpShowsConfiguredHotkeyAndUnconfiguredAlternativeIsNotAnAlias(
		char finishRecording, char unconfiguredKey, string keyName)
	{
		using var host = CreateHost(new ApplicationSettings(availableSources: AudioSourceType.Microphone),
			new ConsoleHotkeySettings(finishRecording: finishRecording));
		var output = await RunWorkflowAsync(host, $"1\n\n{unconfiguredKey}\ns\nq\n");
		Assert.Contains($"  {keyName} - transcribe and show the current dialog", output);
		Assert.True(output.IndexOf("Hotkeys:", StringComparison.Ordinal) < output.IndexOf("Select microphone", StringComparison.Ordinal));
		Assert.Contains("Unknown command", output);
	}

	[Fact]
	public void ApplicationOnlyContextCannotBeInitializedTwice()
	{
		var context = new CaptureSessionContext();
		var application = new AudioApplication(123, "Application", true, [123]);
		context.Initialize(null, application);
		Assert.Null(context.Microphone);
		Assert.Same(application, context.Application);
		Assert.Throws<InvalidOperationException>(() => context.Initialize(null, application));
	}

	[Theory]
	[InlineData("s")]
	[InlineData("q")]
	[InlineData("shutdown")]
	public async Task SessionSavesTextAfterPendingAndFinalRecordingsFinish(string stopCommand)
	{
		var directory = Path.Combine(Path.GetTempPath(), $"aissyst-session-save-{Guid.NewGuid():N}");
		var processor = new SessionFileTextProcessor(directory,
			Microsoft.Extensions.Logging.Abstractions.NullLogger<SessionFileTextProcessor>.Instance);
		using var host = CreateHost(new ApplicationSettings(availableSources: AudioSourceType.Microphone,
			pauseUntilRecordingStops: TimeSpan.FromMinutes(1)), textProcessor: processor);
		var capture = host.Services.GetRequiredService<TestAudioCapture>();
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		host.Services.GetRequiredService<TestTranscriber>().Recognize = async (frames, cancellationToken) =>
		{
			if (frames[0].Samples.Span[0] == 0.5f)
			{
				entered.TrySetResult();
				await release.Task.WaitAsync(cancellationToken);
				return "First fragment.";
			}
			return "Final fragment.";
		};
		var previousInput = Console.In;
		var previousOutput = Console.Out;
		using var input = new ControlledInput();
		using var output = new StringWriter();
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		try
		{
			Console.SetIn(input);
			Console.SetOut(output);
			var worker = host.Services.GetRequiredService<MainThreadWorker>();
			await worker.StartAsync(timeout.Token);
			input.Send("1");
			input.Send("");
			while (capture.MicrophoneCaptures == 0)
			{
				await Task.Delay(10, timeout.Token);
			}
			await capture.EmitMicrophoneAsync(new AudioFrame(Enumerable.Repeat(0.5f, 320).ToArray(),
				new AudioFormat(16000, 1), TimeSpan.Zero));
			input.Send(" ");
			await entered.Task.WaitAsync(timeout.Token);
			await capture.EmitMicrophoneAsync(new AudioFrame(Enumerable.Repeat(0.25f, 320).ToArray(),
				new AudioFormat(16000, 1), TimeSpan.Zero));
			Task stop = Task.CompletedTask;
			if (stopCommand == "shutdown")
			{
				stop = worker.StopAsync(timeout.Token);
			}
			else
			{
				input.Send(stopCommand);
			}
			while (!output.ToString().Contains("Finishing pending transcriptions"))
			{
				await Task.Delay(10, timeout.Token);
			}
			Assert.False(Directory.Exists(directory));
			Assert.False(worker.ExecuteTask!.IsCompleted);
			release.TrySetResult();
			if (stopCommand == "s")
			{
				input.Send("q");
			}
			await worker.ExecuteTask.WaitAsync(timeout.Token);
			await stop.WaitAsync(timeout.Token);
			var session = Assert.Single(Directory.GetDirectories(directory));
			Assert.Equal("First fragment. Final fragment.", await File.ReadAllTextAsync(Path.Combine(session, "microphone.txt")));
			Assert.Empty(await File.ReadAllTextAsync(Path.Combine(session, "application.txt")));
		}
		finally
		{
			release.TrySetResult();
			input.Complete();
			Console.SetIn(previousInput);
			Console.SetOut(previousOutput);
			if (Directory.Exists(directory))
			{
				Directory.Delete(directory, recursive: true);
			}
		}
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task AssistanceHotkeysSendAllFiveRequestTypesAndPrintAnswers(bool whileListening)
	{
		using var host = CreateHost(new ApplicationSettings(availableSources: AudioSourceType.Microphone), useSessionModel: true);
		var connector = host.Services.GetRequiredService<TestLLMConnector>();
		connector.Initialize = (_, _) => Task.FromResult(new DialogResponse());
		var received = Channel.CreateUnbounded<RequestType>();
		connector.Send = (request, _) =>
		{
			received.Writer.TryWrite(request.Request);
			return Task.FromResult(new DialogResponse
			{
				RequestId = request.RequestId, Summary = request.Summary,
				SummaryTimestamp = request.RawRecords.LastOrDefault()?.Timestamp,
				Message = request.Request.ToString()
			});
		};
		var previousInput = Console.In;
		var previousOutput = Console.Out;
		using var input = new ControlledInput();
		using var output = new StringWriter();
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		try
		{
			Console.SetIn(input);
			Console.SetOut(output);
			var worker = host.Services.GetRequiredService<MainThreadWorker>();
			await worker.StartAsync(timeout.Token);
			input.Send("1");
			if (whileListening)
			{
				input.Send("");
			}
			var answers = ((TestOutput)host.Services.GetRequiredService<IOutput>()).Answers;
			var expected = new[] { RequestType.Translate, RequestType.Advice, RequestType.Reply, RequestType.TranslateAndAdvice, RequestType.TranslateAndReply };
			for (var index = 0; index < expected.Length; index++)
			{
				input.Send((index + 1).ToString());
				Assert.Equal(expected[index], await received.Reader.ReadAsync(timeout.Token));
				while (answers.Count < index + 1)
				{
					await Task.Delay(10, timeout.Token);
				}
			}
			input.Send("q");
			await worker.ExecuteTask!.WaitAsync(timeout.Token);
			Assert.Equal(expected.Select(type => type.ToString()), answers);
			Assert.Equal(5, connector.Requests);
			Assert.Contains("1 - translate", output.ToString());
			Assert.Contains("5 - translate and provide a full answer", output.ToString());
			Assert.DoesNotContain("Unknown command", output.ToString());
		}
		finally
		{
			input.Complete();
			Console.SetIn(previousInput);
			Console.SetOut(previousOutput);
		}
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task QuitCancelsPendingAssistanceWithoutBlockingKeyboardInput(bool whileListening)
	{
		using var host = CreateHost(new ApplicationSettings(availableSources: AudioSourceType.Microphone), useSessionModel: true);
		var connector = host.Services.GetRequiredService<TestLLMConnector>();
		connector.Initialize = (_, _) => Task.FromResult(new DialogResponse());
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		connector.Send = async (_, cancellationToken) =>
		{
			entered.TrySetResult();
			try
			{
				await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
			}
			finally
			{
				cancelled.TrySetResult();
			}
			return new DialogResponse();
		};
		var previousInput = Console.In;
		var previousOutput = Console.Out;
		using var input = new ControlledInput();
		using var output = new StringWriter();
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		try
		{
			Console.SetIn(input);
			Console.SetOut(output);
			var worker = host.Services.GetRequiredService<MainThreadWorker>();
			await worker.StartAsync(timeout.Token);
			input.Send("1");
			if (whileListening)
			{
				input.Send("");
			}
			input.Send("2");
			await entered.Task.WaitAsync(timeout.Token);
			input.Send("q");
			await worker.ExecuteTask!.WaitAsync(timeout.Token);
			await cancelled.Task.WaitAsync(timeout.Token);
			Assert.Empty(((TestOutput)host.Services.GetRequiredService<IOutput>()).Answers);
		}
		finally
		{
			input.Complete();
			Console.SetIn(previousInput);
			Console.SetOut(previousOutput);
		}
	}

	private sealed class ControlledInput : TextReader
	{
		private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
		private int _reads;
		internal int Reads => Volatile.Read(ref _reads);
		internal void Send(string command) => _lines.Writer.TryWrite(command);
		internal void Complete() => _lines.Writer.TryComplete();
		public override string? ReadLine()
		{
			Interlocked.Increment(ref _reads);
			try
			{
				return _lines.Reader.ReadAsync().AsTask().GetAwaiter().GetResult();
			}
			catch (ChannelClosedException)
			{
				return null;
			}
		}
	}

	private static IHost CreateHost(ApplicationSettings settings, ConsoleHotkeySettings? hotkeys = null,
		ITextProcessor? textProcessor = null, bool useSessionModel = false)
	{
		var builder = Host.CreateApplicationBuilder();
		builder.Logging.ClearProviders();
		builder.Services.AddSingleton<IApplicationSettings>(settings);
		builder.Services.AddSingleton<TestSessionSelector>();
		builder.Services.AddSingleton<ISessionSelector>(provider => provider.GetRequiredService<TestSessionSelector>());
		builder.Services.AddSingleton(hotkeys ?? new ConsoleHotkeySettings());
		builder.Services.AddSingleton<TestAudioCapture>();
		builder.Services.AddSingleton<IAudioCapture>(provider => provider.GetRequiredService<TestAudioCapture>());
		builder.Services.AddSingleton<TestTranscriber>();
		builder.Services.AddSingleton<ITranscriber>(provider => provider.GetRequiredService<TestTranscriber>());
		if (textProcessor == null)
		{
			builder.Services.AddSingleton<ITextProcessor, TestTextProcessor>();
		}
		else
		{
			builder.Services.AddSingleton(textProcessor);
		}
		builder.Services.AddSingleton<TestLLMConnector>();
		builder.Services.AddSingleton<ILLMConnector>(provider => provider.GetRequiredService<TestLLMConnector>());
		if (useSessionModel)
		{
			builder.Services.AddSingleton<IDatabase, TestSessionDatabase>();
			builder.Services.AddSingleton<SessionModel>();
		}
		builder.Services.AddSingleton<ConsoleCaptureSession>();
		builder.Services.AddSingleton<ICaptureSession>(provider => provider.GetRequiredService<ConsoleCaptureSession>());
		builder.Services.AddSingleton<ApplicationController>();
		builder.Services.AddSingleton<DialogCoordinator>();
		builder.Services.AddSingleton<IOutput, TestOutput>();
		builder.Services.AddScoped<CaptureSessionContext>();
		builder.Services.AddScoped<CaptureSession>();
		builder.Services.AddSingleton<MainThreadWorker>();
		return builder.Build();
	}

	private static async Task<string> RunWorkflowAsync(IHost host, string input)
	{
		var previousInput = Console.In;
		var previousOutput = Console.Out;
		using var reader = new StringReader(input);
		using var output = new StringWriter();
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		try
		{
			Console.SetIn(reader);
			Console.SetOut(output);
			var worker = host.Services.GetRequiredService<MainThreadWorker>();
			await worker.StartAsync(timeout.Token);
			await worker.ExecuteTask!.WaitAsync(timeout.Token);
			return output.ToString();
		}
		finally
		{
			Console.SetIn(previousInput);
			Console.SetOut(previousOutput);
		}
	}
}
