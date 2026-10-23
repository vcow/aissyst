using aissyst.console;
using aissyst.core;
using aissyst.core.Audio;
using R3;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace aissyst.tests;

public class CaptureSessionTests
{
	private readonly ConcurrentQueue<(AudioSourceType Source, IReadOnlyList<AudioFrame> Frames)> _records = new();
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
		Assert.Empty(_records);

		await capture.EmitMicrophoneAsync(Frame(0.5f));
		await capture.EmitApplicationAsync(Frame(-0.5f));
		await session.CompleteRecordingsAsync();
		Assert.Single(_records, record => record.Source == AudioSourceType.Microphone);
		Assert.Single(_records, record => record.Source == AudioSourceType.Application);
		Assert.False(session.Completion.IsCompleted);

		await session.CompleteRecordingsAsync();
		Assert.Equal(2, _records.Count);
		await capture.EmitMicrophoneAsync(Frame(0.25f));
		await session.CompleteRecordingsAsync();
		Assert.Equal(2, _records.Count(record => record.Source == AudioSourceType.Microphone));
		Assert.Equal(3, _records.Count);
		var completed = _records.ToArray();
		Assert.Equal(AudioSourceType.Microphone, completed[0].Source);
		Assert.Equal(AudioSourceType.Application, completed[1].Source);
		var first = Assert.Single(completed[0].Frames);
		Assert.Equal(new AudioFormat(16000, 1), first.Format);
		Assert.All(first.Samples.ToArray(), sample => Assert.Equal(0.5f, sample));
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
		while (_records.IsEmpty)
		{
			await Task.Delay(20, timeout.Token);
		}
		Assert.DoesNotContain(_records, record => record.Source == AudioSourceType.Microphone);
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
		var record = Assert.Single(_records);
		Assert.Equal(AudioSourceType.Microphone, record.Source);
		Assert.Equal(320, Assert.Single(record.Frames).Samples.Length);
	}

	[Fact]
	public async Task DurationLimitPublishesBothSourcesAndKeepsListeningWithoutManualCompletion()
	{
		using var host = CreateHost(maxRecordingDuration: TimeSpan.FromSeconds(1));
		await using var scope = host.Services.CreateAsyncScope();
		scope.ServiceProvider.GetRequiredService<CaptureSessionContext>().Initialize(Microphone, Application);
		var session = scope.ServiceProvider.GetRequiredService<CaptureSession>();
		var capture = host.Services.GetRequiredService<TestAudioCapture>();
		session.Start();
		var format = new AudioFormat(10, 1);
		await capture.EmitMicrophoneAsync(new AudioFrame(Enumerable.Repeat(0.5f, 20).ToArray(), format, TimeSpan.Zero));
		await capture.EmitApplicationAsync(new AudioFrame(Enumerable.Repeat(0.25f, 20).ToArray(), format, TimeSpan.Zero));
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		while (_records.Count < 4)
		{
			await Task.Delay(10, timeout.Token);
		}
		Assert.Equal(2, _records.Count(record => record.Source == AudioSourceType.Microphone));
		Assert.Equal(2, _records.Count(record => record.Source == AudioSourceType.Application));
		Assert.All(_records, record => Assert.Equal(10, Assert.Single(record.Frames).Samples.Length));
		Assert.False(session.Completion.IsCompleted);
	}

	private static AudioFrame Frame(float value)
	{
		return new AudioFrame(Enumerable.Repeat(value, 320).ToArray(), new AudioFormat(16000, 1), TimeSpan.Zero);
	}

	private IHost CreateHost(TimeSpan? silenceTimeout = null, TimeSpan? maxRecordingDuration = null)
	{
		var builder = Host.CreateApplicationBuilder();
		builder.Logging.ClearProviders();
		builder.Services.AddScoped<CaptureSessionContext>();
		builder.Services.AddScoped<CaptureSession>();
		builder.Services.AddSingleton<IApplicationSettings>(new ApplicationSettings(silenceTimeout ?? TimeSpan.FromMinutes(1),
			maxRecordingDuration: maxRecordingDuration));
		builder.Services.AddSingleton(new ConsoleHotkeySettings());
		builder.Services.AddSingleton<ConsoleCaptureSession>();
		builder.Services.AddSingleton<TestAudioCapture>();
		builder.Services.AddSingleton<IAudioCapture>(provider => provider.GetRequiredService<TestAudioCapture>());
		var host = builder.Build();
		var records = host.Services.GetRequiredService<ConsoleCaptureSession>();
		records.MicrophoneRecordObservable.Subscribe(frames => _records.Enqueue((AudioSourceType.Microphone, frames)));
		records.ApplicationRecordingObservable.Subscribe(frames => _records.Enqueue((AudioSourceType.Application, frames)));
		return host;
	}
}
