using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using aissyst.desktop.Settings;
using aissyst.desktop.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace aissyst.desktop.Views;

public partial class SettingsWindow : Window
{
	private readonly DesktopSettingsStore? _store;
	private readonly ILogger<SettingsWindow> _logger = NullLogger<SettingsWindow>.Instance;
	private readonly SettingsViewModel _model = new(new DesktopApplicationSettings());
	private bool _saving;
	private bool _closeRequested;

	public SettingsWindow()
	{
		InitializeComponent();
		DataContext = _model;
	}

	internal SettingsWindow(DesktopSettingsStore store, ILogger<SettingsWindow> logger) : this()
	{
		_store = store;
		_logger = logger;
		_model = new SettingsViewModel(store.Saved);
		DataContext = _model;
	}

	protected override void OnClosing(WindowClosingEventArgs e)
	{
		base.OnClosing(e);
		if (_saving)
		{
			e.Cancel = true;
		}
	}

	private void RestoreDefaults(object? sender, RoutedEventArgs e) => _model.RestoreDefaults();
	private void CancelSettings(object? sender, RoutedEventArgs e) => Close();
	internal void RequestClose()
	{
		_closeRequested = true;
		Close();
	}

	private async void SaveSettings(object? sender, RoutedEventArgs e)
	{
		if (_saving)
		{
			return;
		}
		_saving = true;
		_model.SetEditing(false);
		_model.SetError(string.Empty);
		try
		{
			var store = _store ?? throw new InvalidOperationException("The settings store is unavailable.");
			await store.SaveAsync(_model.GetSettings());
			_logger.LogInformation("Settings saved. Restart the application to apply them.");
			_saving = false;
			Close();
		}
		catch (Exception exception)
		{
			_model.SetError(exception.Message);
			_logger.LogWarning(exception, "Failed to save application settings.");
		}
		finally
		{
			_saving = false;
			_model.SetEditing(true);
			if (_closeRequested)
			{
				Close();
			}
		}
	}

	private async void BrowseModel(object? sender, RoutedEventArgs e)
	{
		_model.SetEditing(false);
		try
		{
			var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
			{
				Title = "Select a transcription model", AllowMultiple = false,
				FileTypeFilter = [new FilePickerFileType("Whisper models") { Patterns = ["*.bin"] }]
			});
			if (files.Count > 0)
			{
				_model.SetModelPath(files[0].TryGetLocalPath()
					?? throw new InvalidOperationException("Select a local model file."));
			}
		}
		catch (Exception exception)
		{
			_model.SetError(exception.Message);
			_logger.LogWarning(exception, "Failed to select a transcription model.");
		}
		finally
		{
			_model.SetEditing(true);
		}
	}
}
