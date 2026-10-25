using aissyst.console;
using aissyst.core;
using aissyst.core.Audio;
using aissyst.core.Controller;
using aissyst.core.LLMConnector;
using aissyst.core.LLMConnector.Model;
using aissyst.core.Output;
using aissyst.core.Processor;
using aissyst.core.Transcription;
using chatgpt_connector;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

if (OperatingSystem.IsMacOS())
{
	// OSX
	builder.Services.AddSingleton<IAudioCapture, aissyst.macos.Audio.AudioCapture>();
}
else if (OperatingSystem.IsWindows())
{
	// WIN_64
	builder.Services.AddSingleton<IAudioCapture, aissyst.windows.Audio.AudioCapture>();
}
else
{
	throw new NotImplementedException("The solution isn't implemented for specified platform.");
}

IApplicationSettings settings = new ApplicationSettings(
	pauseUntilRecordingStops: TimeSpan.FromSeconds(1),
	maxRecordingDuration: TimeSpan.FromSeconds(15),
	availableSources: AudioSourceType.Both,
	maxTranscriptionProcessingCount: 1,
	maxTextProcessingCount: 1);

builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromMinutes(30) });
builder.Services.AddSingleton<ITranscriber, WhisperTranscriber>();
builder.Services.AddSingleton(new ConsoleHotkeySettings(
	finishRecording: ' ',
	stopSession: 'S',
	clearQueue: 'C',
	quit: 'Q'));
builder.Services.AddSingleton<ConsoleCaptureSession>();
builder.Services.AddSingleton<ICaptureSession>(provider => provider.GetRequiredService<ConsoleCaptureSession>());
builder.Services.AddSingleton<ITextProcessor, TextProcessor>();
builder.Services.AddSingleton<IOutput, ConsoleOutput>();
builder.Services.AddSingleton(new ChatGPTConnectorOptions());
builder.Services.AddSingleton<IChatGPTAuthorization, LoopbackChatGPTAuthorization>();
builder.Services.AddSingleton<ILLMConnector, ChatGPTConnector>();
builder.Services.AddSingleton<SessionModel>();
builder.Services.AddSingleton<ApplicationController>();
builder.Services.AddScoped<CaptureSessionContext>();
builder.Services.AddScoped<CaptureSession>();

builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(3));
builder.Services.AddHostedService<MainThreadWorker>();
using var host = builder.Build();

await host.RunAsync();
