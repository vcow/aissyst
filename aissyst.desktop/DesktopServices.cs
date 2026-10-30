using Avalonia.Controls;
using aissyst.core;
using aissyst.core.Audio;
using aissyst.core.Controller;
using aissyst.core.Database;
using aissyst.core.LLMConnector;
using aissyst.core.LLMConnector.Model;
using aissyst.core.Output;
using aissyst.core.Processor;
using aissyst.core.Transcription;
using aissyst.desktop.Logging;
using aissyst.desktop.Selectors;
using aissyst.desktop.ViewModels;
using aissyst.desktop.Wizard;
using aissyst.sqlite;
using chatgpt_connector;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace aissyst.desktop;

internal static class DesktopServices
{
	public static ServiceCollection Create(Window owner, LogViewModel log)
	{
		var services = new ServiceCollection();
		services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Information)
			.AddProvider(new WindowLoggerProvider(log.Append)));
		services.AddSingleton<IWizardService>(provider => new WizardService(() => owner,
			provider.GetRequiredService<ILogger<WizardService>>()));
		services.AddSingleton<IProgress<InitializationProgress>>(provider =>
			new DesktopStartupProgress((WizardService)provider.GetRequiredService<IWizardService>()));
		services.AddSingleton<IApplicationSettings>(new ApplicationSettings(maxRecordingDuration: TimeSpan.FromSeconds(15)));
		services.AddSingleton<IAudioCapture>(_ => OperatingSystem.IsMacOS()
			? new aissyst.macos.Audio.AudioCapture()
			: OperatingSystem.IsWindows() ? new aissyst.windows.Audio.AudioCapture()
			: throw new PlatformNotSupportedException("Audio sources are supported only on macOS and Windows."));
		var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "aissyst", "aissyst.sqlite");
		services.AddSingleton<IDatabase>(_ => new SqliteDatabase(path));
		services.AddSingleton<ISessionSelector, DesktopSessionSelector>();
		services.AddSingleton<IModelSelector, DesktopModelSelector>();
		services.AddSingleton<ICaptureSession, DesktopCaptureSession>();
		services.AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromMinutes(30) });
		services.AddSingleton<ITranscriber, WhisperTranscriber>();
		services.AddSingleton<ITextProcessor, TextProcessor>();
		services.AddSingleton<DialogCoordinator>();
		services.AddSingleton(new SessionRecoveryOptions());
		services.AddSingleton<IOutput, DesktopOutput>();
		services.AddSingleton(new ChatGPTConnectorOptions());
		services.AddSingleton<IChatGPTAuthorization, LoopbackChatGPTAuthorization>();
		services.AddSingleton<ILLMConnector, ChatGPTConnector>();
		services.AddSingleton<SessionModel>();
		services.AddSingleton<ApplicationController>();
		return services;
	}
}
