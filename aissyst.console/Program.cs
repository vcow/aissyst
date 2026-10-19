using aissyst.console;
using aissyst.core.Audio;
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

builder.Services.AddScoped<CaptureSessionContext>();
builder.Services.AddScoped<CaptureSession>();
builder.Services.AddHostedService<MainThreadWorker>();
using var host = builder.Build();

await host.RunAsync();
