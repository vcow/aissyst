using aissyst.core;
using aissyst.core.Audio;
using aissyst.core.Controller;
using aissyst.core.LLMConnector.Model;
using aissyst.desktop.Selectors;
using aissyst.desktop.Wizard;
using Microsoft.Extensions.Logging;

namespace aissyst.desktop.Interview;

internal sealed class DesktopInterview(ApplicationController controller, SessionModel model,
	DesktopCaptureSession sources, IAudioCapture capture, IApplicationSettings settings,
	IWizardService wizard, ILogger<DesktopInterview> logger) : IAsyncDisposable
{
	private readonly CancellationTokenSource _shutdown = new();
	private readonly SemaphoreSlim _startGate = new(1, 1);
	private readonly SemaphoreSlim _forceGate = new(1, 1);
	private AudioCaptureSession? _capture;
	private Task? _monitor;
	private Task? _disposal;
	private volatile bool _started;
	public bool IsInitialized { get; private set; }
	public bool IsStarted => _started;
	public event Action? StateChanged;

	public async Task WaitForStartAsync(CancellationToken cancellationToken = default)
	{
		IsInitialized = true;
		StateChanged?.Invoke();
		var error = string.Empty;
		while (true)
		{
			await wizard.ShowAsync(new WizardPage("Ready to start",
				"Setup is complete. Arrange the windows, then press Start to begin listening and transcribing the interview.", [])
			{
				IsAction = true, ActionLabel = "Start", Error = error
			}, cancellationToken);
			try
			{
				await StartAsync(cancellationToken);
				return;
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _shutdown.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception exception)
			{
				logger.LogError(exception, "Failed to start audio capture.");
				error = $"Could not start listening: {exception.Message}\nPress Start to try again.";
			}
		}
	}

	private async Task StartAsync(CancellationToken cancellationToken)
	{
		using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
		await _startGate.WaitAsync(cancellation.Token);
		try
		{
			if (_started)
			{
				return;
			}
			if (!model.IsReady)
			{
				throw new InvalidOperationException("The LLM session is not ready. Reconnect before starting.");
			}
			var session = new AudioCaptureSession(capture, settings, sources.SelectedMicrophone,
				sources.SelectedApplication, sources.Publish, logger, _shutdown.Token);
			try
			{
				await Task.Run(session.Start, cancellation.Token);
				if (session.Completion.IsCompleted)
				{
					await session.Completion;
					throw new IOException("Audio capture ended before listening could start.");
				}
				cancellation.Token.ThrowIfCancellationRequested();
			}
			catch
			{
				try { await session.DisposeAsync(); }
				catch (Exception) { /* Preserve the startup failure. */ }
				throw;
			}
			_capture = session;
			_started = true;
			_monitor = ObserveCaptureAsync(session);
			StateChanged?.Invoke();
			logger.LogInformation("Interview started. Listening to the selected audio sources.");
		}
		finally
		{
			_startGate.Release();
		}
	}

	private async Task ObserveCaptureAsync(AudioCaptureSession session)
	{
		try
		{
			await session.Completion;
		}
		catch (Exception exception)
		{
			logger.LogError(exception, "Audio capture stopped unexpectedly. Restart the application to listen again.");
		}
		finally
		{
			_started = false;
			StateChanged?.Invoke();
		}
	}

	public async Task ForceDialogAsync(CancellationToken cancellationToken = default)
	{
		using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
		if (!await _forceGate.WaitAsync(0, cancellation.Token))
		{
			return;
		}
		try
		{
			var session = _started ? _capture : null;
			if (session == null)
			{
				return;
			}
			await controller.ShowDialogAsync(cancellation.Token, session.PauseAndCompleteRecordingsAsync);
		}
		finally
		{
			_forceGate.Release();
		}
	}

	public Task ReconnectAsync(CancellationToken cancellationToken = default) => model.ReconnectAsync(cancellationToken);

	public ValueTask DisposeAsync() => new(_disposal ??= DisposeCoreAsync());

	private async Task DisposeCoreAsync()
	{
		await _shutdown.CancelAsync();
		await _startGate.WaitAsync();
		try
		{
			if (_capture != null)
			{
				try { await _capture.DisposeAsync(); }
				catch (Exception exception) { logger.LogWarning(exception, "Audio capture failed while stopping."); }
			}
			if (_monitor != null)
			{
				await _monitor;
			}
			await _forceGate.WaitAsync();
			_forceGate.Release();
			await controller.WaitForIdleAsync();
		}
		finally
		{
			_startGate.Release();
			_startGate.Dispose();
			_forceGate.Dispose();
			_shutdown.Dispose();
		}
	}
}
