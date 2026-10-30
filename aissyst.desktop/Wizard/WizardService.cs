using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using aissyst.desktop.ViewModels;
using aissyst.desktop.Views;
using Microsoft.Extensions.Logging;

namespace aissyst.desktop.Wizard;

internal sealed class WizardService(Func<Window> owner, ILogger<WizardService> logger) : IWizardService, IAsyncDisposable
{
	private readonly SemaphoreSlim _gate = new(1, 1);
	private readonly CancellationTokenSource _cancel = new();
	private readonly WizardViewModel _model = new();
	private WizardWindow? _window;
	private TaskCompletionSource<string>? _pending;
	private bool _completed;
	private readonly Lock _progressSync = new();
	private long _pageVersion;
	private ProgressUpdate? _pendingProgress;
	private bool _progressPosted;
	private readonly record struct ProgressUpdate(long PageVersion, string Message, long? CompletedBytes, long? TotalBytes);
	internal WizardWindow? Window => _window;

	public CancellationToken CancellationToken => _cancel.Token;

	public async Task<string> ShowAsync(WizardPage page, CancellationToken cancellationToken = default)
	{
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancel.Token);
		await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
		var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
		using var registration = linked.Token.Register(() => completion.TrySetCanceled(linked.Token));
		try
		{
			await Dispatcher.UIThread.InvokeAsync(() =>
			{
				linked.Token.ThrowIfCancellationRequested();
				ObjectDisposedException.ThrowIf(_completed, this);
				if (_window == null)
				{
					_window = new WizardWindow { DataContext = _model };
					_window.NextRequested += () => Submit(false);
					_window.SecondaryRequested += () => Submit(true);
					_window.BrowseRequested += Browse;
					_window.Closed += (_, _) =>
					{
						if (!_completed)
						{
							_cancel.Cancel();
						}
					};
					DesktopWindowPlacement.Show(owner(), _window);
				}
				_pending = completion;
				InvalidateProgress();
				_model.Show(page);
				_window.Activate();
			});
			var result = await completion.Task.ConfigureAwait(false);
			linked.Token.ThrowIfCancellationRequested();
			return result;
		}
		finally
		{
			await Dispatcher.UIThread.InvokeAsync(() =>
			{
				if (ReferenceEquals(_pending, completion))
				{
					_pending = null;
					if (!_model.IsWaiting)
					{
						InvalidateProgress();
						_model.Wait();
					}
				}
			});
			_gate.Release();
		}
	}

	private void Submit(bool secondary)
	{
		if (_pending is { Task.IsCompleted: false } pending && _model.TrySubmit(out var value, secondary))
		{
			InvalidateProgress();
			pending.TrySetResult(value);
		}
	}

	public void ReportProgress(string message, long? completedBytes = null, long? totalBytes = null)
	{
		lock (_progressSync)
		{
			_pendingProgress = new ProgressUpdate(_pageVersion, message, completedBytes, totalBytes);
			if (_progressPosted)
			{
				return;
			}
			_progressPosted = true;
		}
		Dispatcher.UIThread.Post(ApplyProgress, DispatcherPriority.Background);
	}

	private void ApplyProgress()
	{
		ProgressUpdate? progress;
		long version;
		lock (_progressSync)
		{
			progress = _pendingProgress;
			version = _pageVersion;
			_pendingProgress = null;
			_progressPosted = false;
		}
		if (!_completed && progress is { } update && update.PageVersion == version)
		{
			_model.ReportProgress(update.Message, update.CompletedBytes, update.TotalBytes);
		}
	}

	private void InvalidateProgress()
	{
		lock (_progressSync)
		{
			_pageVersion++;
			_pendingProgress = null;
		}
	}

	private async void Browse()
	{
		if (_window == null || _model.IsWaiting || _model.IsBrowsing)
		{
			return;
		}
		_model.IsBrowsing = true;
		try
		{
			var files = await _window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
			{
				Title = "Select a context template", AllowMultiple = false,
				FileTypeFilter = [new FilePickerFileType("ZIP archives") { Patterns = ["*.zip"] }]
			});
			if (!_completed && !_cancel.IsCancellationRequested && files.Count > 0)
			{
				_model.Path = files[0].TryGetLocalPath()
					?? throw new InvalidOperationException("Select a local ZIP file.");
			}
		}
		catch (Exception exception)
		{
			logger.LogError(exception, "Failed to select a context template.");
			_model.ShowError(exception.Message);
		}
		finally
		{
			_model.IsBrowsing = false;
		}
	}

	public async Task CompleteAsync()
	{
		await Dispatcher.UIThread.InvokeAsync(() =>
		{
			_completed = true;
			InvalidateProgress();
			_pending?.TrySetCanceled();
			_window?.Complete();
		});
	}

	public async Task FailAsync(string error)
	{
		await Dispatcher.UIThread.InvokeAsync(() =>
		{
			InvalidateProgress();
			_model.Fail(error);
		});
	}

	public async ValueTask DisposeAsync()
	{
		await _cancel.CancelAsync();
		await CompleteAsync();
		await _gate.WaitAsync();
		_gate.Release();
		_cancel.Dispose();
		_gate.Dispose();
	}
}
