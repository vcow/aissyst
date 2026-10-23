using aissyst.console;
using aissyst.core;
using aissyst.core.Audio;
using aissyst.core.Transcription;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
		var previousInput = Console.In;
		var previousOutput = Console.Out;
		using var output = new StringWriter();
		using var reader = new StringReader(input);
		using var host = CreateHost();
		var capture = new TestAudioCapture();
		var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
		await using var transcriber = new TestTranscriber();
		using var worker = new MainThreadWorker(capture, host.Services.GetRequiredService<IServiceScopeFactory>(),
			lifetime, new ApplicationSettings(availableSources: sources), transcriber,
			host.Services.GetRequiredService<TranscriptionQueue>(), new ConsoleHotkeySettings());
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		try
		{
			Console.SetIn(reader);
			Console.SetOut(output);
			await worker.StartAsync(timeout.Token);
			await worker.ExecuteTask!.WaitAsync(timeout.Token);
		}
		finally
		{
			Console.SetIn(previousInput);
			Console.SetOut(previousOutput);
		}

		Assert.Equal(microphoneSelections, capture.MicrophoneEnumerations);
		Assert.Equal(applicationSelections, capture.ApplicationEnumerations);
		Assert.Equal(microphoneSelections != 0, output.ToString().Contains("Select microphone by number"));
		Assert.Equal(applicationSelections != 0, output.ToString().Contains("Select application by number"));
		Assert.True(lifetime.ApplicationStopping.IsCancellationRequested);
		Assert.Equal(sources != AudioSourceType.None, transcriber.IsReady);
		if (sources == AudioSourceType.None)
		{
			Assert.Contains("No audio sources are enabled", output.ToString());
			Assert.DoesNotContain("start a session", output.ToString());
		}
	}

	[Theory]
	[InlineData(AudioSourceType.Microphone, 1, 0)]
	[InlineData(AudioSourceType.Application, 0, 1)]
	[InlineData(AudioSourceType.Both, 1, 1)]
	public async Task SessionOpensOnlyEnabledSources(AudioSourceType sources, int microphones, int applications)
	{
		using var host = CreateHost();
		var context = new CaptureSessionContext();
		context.Initialize(microphones == 1 ? new AudioInputDevice("mic", "Microphone", 1, true) : null,
			applications == 1 ? new AudioApplication(123, "Application", true, [123]) : null);
		var capture = new TestAudioCapture();
		var directory = Path.Combine(Path.GetTempPath(), $"aissyst-source-tests-{Guid.NewGuid():N}");
		try
		{
			await using var session = new CaptureSession(context,
				host.Services.GetRequiredService<IHostApplicationLifetime>(), capture,
				new ApplicationSettings(availableSources: sources), host.Services.GetRequiredService<TranscriptionQueue>(), directory);
			session.Start();
			await session.CompleteRecordingsAsync().WaitAsync(TimeSpan.FromSeconds(5));
			Assert.Equal(microphones, capture.MicrophoneCaptures);
			Assert.Equal(applications, capture.ApplicationCaptures);
			Assert.False(session.Completion.IsCompleted);
		}
		finally
		{
			if (Directory.Exists(directory))
			{
				Directory.Delete(directory, recursive: true);
			}
		}
	}

	[Fact]
	public async Task CustomClearHotkeyWorksDuringSessionAndWhileWaitingForRestart()
	{
		var directory = Path.Combine(Path.GetTempPath(), $"aissyst-hotkey-tests-{Guid.NewGuid():N}");
		var settings = new ApplicationSettings(availableSources: AudioSourceType.Microphone);
		var queue = new TranscriptionQueue(settings, NullLogger<TranscriptionQueue>.Instance);
		for (var index = 0; index < 2; index++)
		{
			queue.Enqueue(new TranscriptionRequest(Guid.NewGuid(), "previous-session", AudioSourceType.Application,
				DateTimeOffset.UtcNow, new AudioRecording(new float[] { 0.5f }, new AudioFormat(16000, 1))));
		}
		var builder = Host.CreateApplicationBuilder();
		builder.Logging.ClearProviders();
		var capture = new TestAudioCapture();
		builder.Services.AddScoped<CaptureSessionContext>();
		builder.Services.AddScoped(provider => new CaptureSession(
			provider.GetRequiredService<CaptureSessionContext>(),
			provider.GetRequiredService<IHostApplicationLifetime>(), capture, settings, queue, directory));
		using var host = builder.Build();
		await using var transcriber = new TestTranscriber();
		var hotkeys = new ConsoleHotkeySettings(finishRecording: 'F', stopSession: 'T', clearQueue: 'X', quit: 'Z');
		using var worker = new MainThreadWorker(capture, host.Services.GetRequiredService<IServiceScopeFactory>(),
			host.Services.GetRequiredService<IHostApplicationLifetime>(), settings, transcriber, queue, hotkeys);
		var previousInput = Console.In;
		var previousOutput = Console.Out;
		using var input = new StringReader("1\n\nx\nt\nx\nz\n");
		using var output = new StringWriter();
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		try
		{
			Console.SetIn(input);
			Console.SetOut(output);
			await worker.StartAsync(timeout.Token);
			await worker.ExecuteTask!.WaitAsync(timeout.Token);
			Assert.Equal(1, capture.MicrophoneCaptures);
			Assert.Equal(0, queue.Count);
			Assert.Contains("Removed 2 pending recording(s)", output.ToString());
			Assert.Contains("Removed 0 pending recording(s)", output.ToString());
			Assert.Contains("F to finish active recordings", output.ToString());
			Assert.Contains("X to clear the transcription queue", output.ToString());
			Assert.DoesNotContain("Unknown command", output.ToString());
		}
		finally
		{
			Console.SetIn(previousInput);
			Console.SetOut(previousOutput);
			if (Directory.Exists(directory))
			{
				Directory.Delete(directory, recursive: true);
			}
		}
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

	private static IHost CreateHost()
	{
		var builder = Host.CreateApplicationBuilder();
		builder.Logging.ClearProviders();
		builder.Services.AddSingleton<IApplicationSettings>(new ApplicationSettings());
		builder.Services.AddSingleton<TranscriptionQueue>();
		return builder.Build();
	}

	private sealed class TestTranscriber : ITranscriber
	{
		public Task<string> TranscribeAsync(IReadOnlyList<AudioFrame> frames, CancellationToken cancellationToken = default)
		{
			throw new NotImplementedException();
		}

		public bool IsReady { get; private set; }
		public Task InitializeAsync(CancellationToken cancellationToken = default)
		{
			IsReady = true;
			return Task.CompletedTask;
		}
		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}
}
