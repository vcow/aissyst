using aissyst.console;
using aissyst.core;
using aissyst.core.Audio;
using aissyst.core.Transcription;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace aissyst.tests;

public class CaptureSessionTests : IDisposable
{
	private readonly string _outputDirectory = Path.Combine(Path.GetTempPath(), $"aissyst-recording-tests-{Guid.NewGuid():N}");
	private static readonly AudioInputDevice Microphone = new("test-microphone", "Test microphone", 1, true);
	private static readonly AudioApplication Application = new(123, "Test application", false, new[] { 123 });

	[Fact]
	public async Task DisposingScopeStopsSessionAndNewScopeAllowsRestartWithoutStoppingHost()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		using var host = CreateHost();
		await host.StartAsync(timeout.Token);
		try
		{
			CaptureSession? previousSession = null;
			CaptureSessionContext? previousContext = null;
			for (var index = 0; index < 2; index++)
			{
				CaptureSession session;
				await using (var scope = host.Services.CreateAsyncScope())
				{
					var context = scope.ServiceProvider.GetRequiredService<CaptureSessionContext>();
					context.Initialize(Microphone, Application);
					session = scope.ServiceProvider.GetRequiredService<CaptureSession>();
					Assert.Same(context, session.Context);
					Assert.Same(session, scope.ServiceProvider.GetRequiredService<CaptureSession>());
					Assert.NotSame(previousSession, session);
					Assert.NotSame(previousContext, context);
					session.Start();
					Assert.False(session.Completion.IsCompleted);
					previousSession = session;
					previousContext = context;
				}

				await session.Completion.WaitAsync(timeout.Token);
				Assert.True(session.Completion.IsCompletedSuccessfully);
				Assert.False(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
			}
		}
		finally
		{
			await host.StopAsync(timeout.Token);
		}
	}

	[Fact]
	public async Task SeparateScopesHaveIndependentSourcesAndSessionLifetimes()
	{
		using var host = CreateHost();
		await using var firstScope = host.Services.CreateAsyncScope();
		await using var secondScope = host.Services.CreateAsyncScope();
		var otherMicrophone = new AudioInputDevice("other-microphone", "Other microphone", 2, false);
		var otherApplication = new AudioApplication(456, "Other application", true, new[] { 456 });
		firstScope.ServiceProvider.GetRequiredService<CaptureSessionContext>().Initialize(Microphone, Application);
		secondScope.ServiceProvider.GetRequiredService<CaptureSessionContext>().Initialize(otherMicrophone, otherApplication);
		var first = firstScope.ServiceProvider.GetRequiredService<CaptureSession>();
		var second = secondScope.ServiceProvider.GetRequiredService<CaptureSession>();
		first.Start();
		second.Start();

		Assert.Same(Microphone, first.Context.Microphone);
		Assert.Same(Application, first.Context.Application);
		Assert.Same(otherMicrophone, second.Context.Microphone);
		Assert.Same(otherApplication, second.Context.Application);
		await firstScope.DisposeAsync();
		Assert.True(first.Completion.IsCompletedSuccessfully);
		Assert.False(second.Completion.IsCompleted);
	}

	[Fact]
	public async Task HostShutdownStopsSessionBeforeItsScopeIsDisposed()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		using var host = CreateHost();
		await host.StartAsync(timeout.Token);
		try
		{
			await using var scope = host.Services.CreateAsyncScope();
			scope.ServiceProvider.GetRequiredService<CaptureSessionContext>().Initialize(Microphone, Application);
			var session = scope.ServiceProvider.GetRequiredService<CaptureSession>();
			session.Start();
			Assert.False(session.Completion.IsCompleted);

			await host.StopAsync(timeout.Token);
			await session.Completion.WaitAsync(timeout.Token);
			Assert.True(session.Completion.IsCompletedSuccessfully);
		}
		finally
		{
			await host.StopAsync(timeout.Token);
		}
	}

	[Fact]
	public async Task ManualCompletionIgnoresSilenceAndCompletesBothStreamsWithoutStoppingListening()
	{
		using var host = CreateHost();
		await using var scope = host.Services.CreateAsyncScope();
		scope.ServiceProvider.GetRequiredService<CaptureSessionContext>().Initialize(Microphone, Application);
		var session = scope.ServiceProvider.GetRequiredService<CaptureSession>();
		var capture = host.Services.GetRequiredService<TestAudioCapture>();
		session.Start();
		await session.CompleteRecordingsAsync();
		await capture.EmitMicrophoneAsync(Frame(0));
		await capture.EmitApplicationAsync(Frame(0.001f));
		await session.CompleteRecordingsAsync();
		Assert.Empty(Directory.GetFiles(_outputDirectory, "*.wav"));

		await capture.EmitMicrophoneAsync(Frame(0.5f));
		await capture.EmitApplicationAsync(Frame(-0.5f));
		await session.CompleteRecordingsAsync();
		Assert.Single(Directory.GetFiles(_outputDirectory, "microphone-*.wav"));
		Assert.Single(Directory.GetFiles(_outputDirectory, "application-*.wav"));
		Assert.False(session.Completion.IsCompleted);

		await session.CompleteRecordingsAsync();
		Assert.Equal(2, Directory.GetFiles(_outputDirectory, "*.wav").Length);
		await capture.EmitMicrophoneAsync(Frame(0.25f));
		await session.CompleteRecordingsAsync();
		Assert.Equal(2, Directory.GetFiles(_outputDirectory, "microphone-*.wav").Length);
		var queue = host.Services.GetRequiredService<TranscriptionQueue>();
		Assert.Equal(3, queue.Count);
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		var first = await queue.DequeueAsync(timeout.Token);
		var second = await queue.DequeueAsync(timeout.Token);
		Assert.Equal(AudioSourceType.Microphone, first.Source);
		Assert.Equal(AudioSourceType.Application, second.Source);
		Assert.Equal(first.SessionId, second.SessionId);
		Assert.NotEqual(first.Id, second.Id);
		Assert.Equal(new AudioFormat(16000, 1), first.Recording.Format);
		Assert.All(first.Recording.Samples.ToArray(), sample => Assert.Equal(0.5f, sample));
	}

	[Fact]
	public async Task SilenceTimeoutCompletesApplicationEvenIfItStopsDeliveringFrames()
	{
		using var host = CreateHost(TimeSpan.FromMilliseconds(80));
		await using var scope = host.Services.CreateAsyncScope();
		scope.ServiceProvider.GetRequiredService<CaptureSessionContext>().Initialize(Microphone, Application);
		var session = scope.ServiceProvider.GetRequiredService<CaptureSession>();
		session.Start();
		await host.Services.GetRequiredService<TestAudioCapture>().EmitApplicationAsync(Frame(0.5f));
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		while (Directory.GetFiles(_outputDirectory, "application-*.wav").Length == 0)
		{
			await Task.Delay(20, timeout.Token);
		}
		Assert.Empty(Directory.GetFiles(_outputDirectory, "microphone-*.wav"));
		Assert.False(session.Completion.IsCompleted);
	}

	[Fact]
	public async Task DisposingSessionFlushesAnUnfinishedRecording()
	{
		using var host = CreateHost();
		await using (var scope = host.Services.CreateAsyncScope())
		{
			scope.ServiceProvider.GetRequiredService<CaptureSessionContext>().Initialize(Microphone, Application);
			var session = scope.ServiceProvider.GetRequiredService<CaptureSession>();
			session.Start();
			await host.Services.GetRequiredService<TestAudioCapture>().EmitMicrophoneAsync(Frame(0.5f));
		}
		var path = Assert.Single(Directory.GetFiles(_outputDirectory, "microphone-*.wav"));
		var bytes = await File.ReadAllBytesAsync(path);
		Assert.Equal(44 + 320 * sizeof(short), bytes.Length);
		Assert.Empty(Directory.GetFiles(_outputDirectory, "*.tmp"));
	}

	private static AudioFrame Frame(float value)
	{
		return new AudioFrame(Enumerable.Repeat(value, 320).ToArray(), new AudioFormat(16000, 1), TimeSpan.Zero);
	}

	private IHost CreateHost(TimeSpan? silenceTimeout = null)
	{
		var builder = Host.CreateApplicationBuilder();
		builder.Logging.ClearProviders();
		builder.Services.AddScoped<CaptureSessionContext>();
		builder.Services.AddScoped(provider => new CaptureSession(
			provider.GetRequiredService<CaptureSessionContext>(),
			provider.GetRequiredService<IHostApplicationLifetime>(),
			provider.GetRequiredService<IAudioCapture>(),
			provider.GetRequiredService<IApplicationSettings>(),
			provider.GetRequiredService<TranscriptionQueue>(), _outputDirectory));
		builder.Services.AddSingleton<IApplicationSettings>(new ApplicationSettings(silenceTimeout ?? TimeSpan.FromMinutes(1)));
		builder.Services.AddSingleton<TranscriptionQueue>();
		builder.Services.AddSingleton<TestAudioCapture>();
		builder.Services.AddSingleton<IAudioCapture>(provider => provider.GetRequiredService<TestAudioCapture>());
		return builder.Build();
	}

	public void Dispose()
	{
		if (Directory.Exists(_outputDirectory))
		{
			Directory.Delete(_outputDirectory, recursive: true);
		}
	}
}
