using System.Text;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using aissyst.core.Processor;
using aissyst.desktop.ViewModels;
using aissyst.desktop.Views;
using Microsoft.Extensions.Logging;

namespace aissyst.desktop.Dialog;

internal sealed class DialogSaveTarget(string name, Func<Task<Stream>> openWrite, Action dispose) : IDisposable
{
	public string Name => name;
	public Task<Stream> OpenWriteAsync() => openWrite();
	public void Dispose() => dispose();
}

internal sealed class DesktopDialogService(DialogViewModel model, ILogger<DesktopDialogService> logger,
	Func<FilePickerSaveOptions, Task<DialogSaveTarget?>>? savePicker = null) : IAsyncDisposable
{
	private readonly CancellationTokenSource _shutdown = new();
	private bool _closing;
	private Task? _saving;
	private Task? _disposal;
	internal DialogWindow? Window { get; private set; }

	public void Show()
	{
		if (_closing)
		{
			return;
		}
		if (Window == null)
		{
			Window = new DialogWindow { DataContext = model };
			Window.Closing += (_, args) =>
			{
				if (!_closing)
				{
					args.Cancel = true;
					Window.Hide();
				}
			};
		}
		Window.Show();
		if (Window.WindowState == WindowState.Minimized)
		{
			Window.WindowState = WindowState.Normal;
		}
		Window.Activate();
	}

	public Task SaveAsync()
	{
		if (_closing)
		{
			return Task.CompletedTask;
		}
		if (_saving is { IsCompleted: false })
		{
			return _saving;
		}
		var snapshot = model.GetSnapshot();
		return _saving = SaveCoreAsync(snapshot);
	}

	private async Task SaveCoreAsync(DialogMessage[] snapshot)
	{
		Show();
		var token = _shutdown.Token;
		var options = new FilePickerSaveOptions
		{
			Title = "Save Dialog", SuggestedFileName = $"dialog-{DateTime.Now:yyyy-MM-dd-HHmmss}.txt",
			DefaultExtension = "txt", FileTypeChoices = [FilePickerFileTypes.TextPlain], ShowOverwritePrompt = true
		};
		using var file = await PickFileAsync(options).WaitAsync(token);
		if (file == null)
		{
			return;
		}
		var text = await Task.Run(() => DialogViewModel.Format(snapshot), token);
		await using (var stream = await file.OpenWriteAsync())
		{
			token.ThrowIfCancellationRequested();
			if (stream.CanSeek)
			{
				stream.SetLength(0);
				stream.Position = 0;
			}
			await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
			await writer.WriteAsync(text.AsMemory(), token);
			await writer.FlushAsync(token);
		}
		logger.LogInformation("Saved {Count} dialog messages to {FileName}.", snapshot.Length, file.Name);
	}

	private async Task<DialogSaveTarget?> PickFileAsync(FilePickerSaveOptions options)
	{
		DialogSaveTarget? file;
		if (savePicker != null)
		{
			file = await savePicker(options);
		}
		else
		{
			var selected = await Window!.StorageProvider.SaveFilePickerAsync(options);
			file = selected == null ? null : new DialogSaveTarget(selected.Name, selected.OpenWriteAsync, selected.Dispose);
		}
		if (_shutdown.IsCancellationRequested)
		{
			file?.Dispose();
			return null;
		}
		return file;
	}

	public ValueTask DisposeAsync() => new(_disposal ??= DisposeCoreAsync());

	private async Task DisposeCoreAsync()
	{
		_closing = true;
		await _shutdown.CancelAsync();
		await Dispatcher.UIThread.InvokeAsync(() => Window?.Close());
		if (_saving != null)
		{
			try
			{
				await _saving;
			}
			catch (Exception)
			{
				// The menu command reports save failures; cancellation is expected during shutdown.
			}
		}
		_shutdown.Dispose();
	}
}
