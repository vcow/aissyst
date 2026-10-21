using aissyst.console;
using aissyst.core;
using aissyst.core.Audio;
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
		var previousInput = Console.In;
		var previousOutput = Console.Out;
		using var output = new StringWriter();
		using var reader = new StringReader(input);
		using var host = CreateHost();
		var capture = new TestAudioCapture();
		var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
		using var worker = new MainThreadWorker(capture, host.Services.GetRequiredService<IServiceScopeFactory>(),
			lifetime, new ApplicationSettings(availableSources: sources));
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
				new ApplicationSettings(availableSources: sources), directory);
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
		return builder.Build();
	}
}
