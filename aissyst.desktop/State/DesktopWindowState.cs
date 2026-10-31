using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace aissyst.desktop.State;

internal sealed record SavedWindowState(int X, int Y, bool? IsVisible, double? Width = null, double? Height = null);

internal sealed class DesktopWindowState : IAsyncDisposable
{
	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
	private readonly Lock _sync = new();
	private readonly SemaphoreSlim _saveGate = new(1, 1);
	private readonly CancellationTokenSource _stopSave = new();
	private readonly Dictionary<Window, TrackedWindow> _tracked = [];
	private readonly Dictionary<string, SavedWindowState> _windows;
	private readonly string? _filePath;
	private long _revision;
	private long _savedRevision;
	private Task? _pendingSave;
	private Task? _disposal;
	private bool _frozen;
	private bool _stopping;
	public Exception? LoadError { get; }
	public event Action<Exception>? SaveFailed;

	public DesktopWindowState(string? filePath = null)
	{
		_filePath = filePath;
		_windows = Load(filePath, out var error);
		LoadError = error;
	}

	public SavedWindowState? Get(string name)
	{
		lock (_sync)
		{
			return _windows.GetValueOrDefault(name);
		}
	}

	public bool ShouldShow(string name) => Get(name)?.IsVisible ?? true;

	public void FitToScreens()
	{
		Dispatcher.UIThread.VerifyAccess();
		if (!_frozen)
		{
			foreach (var tracked in _tracked.Values.ToArray())
			{
				tracked.Fit(tracked.Window.Screens.All.Select(screen => new ScreenArea(screen.WorkingArea, screen.Scaling)).ToArray());
			}
		}
	}

	internal void FitToScreens(IReadOnlyList<ScreenArea> screens)
	{
		Dispatcher.UIThread.VerifyAccess();
		if (!_frozen)
		{
			foreach (var tracked in _tracked.Values.ToArray())
			{
				tracked.Fit(screens);
			}
		}
	}

	public void Attach(Window window, string name, bool rememberVisibility = true)
	{
		Dispatcher.UIThread.VerifyAccess();
		if (_frozen || _tracked.ContainsKey(window))
		{
			return;
		}
		var saved = Get(name);
		if (saved != null)
		{
			if (window.CanResize && IsValidSize(saved.Width, saved.Height))
			{
				window.Width = saved.Width!.Value;
				window.Height = saved.Height!.Value;
			}
			window.WindowStartupLocation = WindowStartupLocation.Manual;
			window.Position = new PixelPoint(saved.X, saved.Y);
		}
		_tracked.Add(window, new TrackedWindow(this, window, name, rememberVisibility));
	}

	private static bool IsValidSize(double? width, double? height) =>
		width is > 0 && height is > 0 && double.IsFinite(width.Value) && double.IsFinite(height.Value);

	private void Record(string name, SavedWindowState value)
	{
		lock (_sync)
		{
			if (_frozen || _windows.GetValueOrDefault(name) == value)
			{
				return;
			}
			_windows[name] = value;
			_revision++;
			if (_filePath != null && _pendingSave == null)
			{
				_pendingSave = SaveAfterMovingAsync();
			}
		}
	}

	private async Task SaveAfterMovingAsync()
	{
		var retryIfChanged = true;
		try
		{
			while (true)
			{
				long revision;
				lock (_sync)
				{
					revision = _revision;
				}
				await Task.Delay(500, _stopSave.Token).ConfigureAwait(false);
				lock (_sync)
				{
					if (revision != _revision)
					{
						continue;
					}
				}
				await FlushAsync().ConfigureAwait(false);
				lock (_sync)
				{
					if (_savedRevision == _revision)
					{
						return;
					}
				}
			}
		}
		catch (OperationCanceledException) when (_stopSave.IsCancellationRequested)
		{
		}
		catch (Exception exception)
		{
			retryIfChanged = false;
			SaveFailed?.Invoke(exception);
		}
		finally
		{
			lock (_sync)
			{
				_pendingSave = null;
				if (!_stopping && retryIfChanged && _savedRevision != _revision)
				{
					_pendingSave = SaveAfterMovingAsync();
				}
			}
		}
	}

	public async Task FlushAsync()
	{
		await _saveGate.WaitAsync().ConfigureAwait(false);
		string? temporary = null;
		try
		{
			Dictionary<string, SavedWindowState> snapshot;
			long revision;
			lock (_sync)
			{
				if (_revision == _savedRevision)
				{
					return;
				}
				snapshot = new(_windows);
				revision = _revision;
			}
			if (_filePath != null)
			{
				Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_filePath))!);
				temporary = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
				await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(snapshot, JsonOptions)).ConfigureAwait(false);
				File.Move(temporary, _filePath, overwrite: true);
			}
			lock (_sync)
			{
				_savedRevision = revision;
			}
		}
		finally
		{
			try
			{
				if (temporary != null && File.Exists(temporary))
				{
					File.Delete(temporary);
				}
			}
			finally
			{
				_saveGate.Release();
			}
		}
	}

	public void StopTracking()
	{
		Dispatcher.UIThread.VerifyAccess();
		if (_frozen)
		{
			return;
		}
		foreach (var tracked in _tracked.Values)
		{
			tracked.Capture();
			tracked.Dispose();
		}
		_tracked.Clear();
		_frozen = true;
	}

	public ValueTask DisposeAsync() => new(_disposal ??= DisposeCoreAsync());

	private async Task DisposeCoreAsync()
	{
		await Dispatcher.UIThread.InvokeAsync(StopTracking);
		Task? pending;
		lock (_sync)
		{
			_stopping = true;
			pending = _pendingSave;
		}
		await _stopSave.CancelAsync();
		if (pending != null)
		{
			await pending.ConfigureAwait(false);
		}
		try
		{
			await FlushAsync().ConfigureAwait(false);
		}
		catch (Exception exception)
		{
			SaveFailed?.Invoke(exception);
		}
		_stopSave.Dispose();
		_saveGate.Dispose();
	}

	private static Dictionary<string, SavedWindowState> Load(string? path, out Exception? error)
	{
		error = null;
		try
		{
			if (path != null && File.Exists(path))
			{
				var data = JsonSerializer.Deserialize<Dictionary<string, SavedWindowState>>(File.ReadAllText(path))
					?? throw new InvalidDataException("The window state file contains no state.");
				if (data.Any(item => string.IsNullOrWhiteSpace(item.Key) || item.Value == null))
				{
					throw new InvalidDataException("The window state file contains an invalid window entry.");
				}
				return data;
			}
		}
		catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
		{
			error = exception;
		}
		return [];
	}

	private sealed class TrackedWindow : IDisposable
	{
		private readonly DesktopWindowState _store;
		private readonly Window _window;
		private readonly string _name;
		private readonly bool _visibility;
		private PixelPoint _position;
		private Size? _size;
		private bool _opened;
		private bool _fitting;
		public Window Window => _window;

		public TrackedWindow(DesktopWindowState store, Window window, string name, bool visibility)
		{
			_store = store;
			_window = window;
			_name = name;
			_visibility = visibility;
			_position = window.Position;
			var saved = store.Get(name);
			if (window.CanResize && saved != null && IsValidSize(saved.Width, saved.Height))
			{
				_size = new Size(saved.Width!.Value, saved.Height!.Value);
			}
			_opened = window.IsVisible;
			window.Opened += Opened;
			window.PositionChanged += Moved;
			window.Resized += Resized;
			window.PropertyChanged += Changed;
			window.Closed += Closed;
		}

		private void Opened(object? sender, EventArgs e)
		{
			_opened = true;
			Capture();
		}
		private void Moved(object? sender, PixelPointEventArgs e) => Capture();
		private void Resized(object? sender, WindowResizedEventArgs e) => Capture();
		private void Changed(object? sender, AvaloniaPropertyChangedEventArgs e)
		{
			if (e.Property == Visual.IsVisibleProperty || e.Property == Window.WindowStateProperty)
			{
				Capture();
			}
		}
		public void Capture()
		{
			if (!_opened || _fitting)
			{
				return;
			}
			if (_window.IsVisible && _window.WindowState == WindowState.Normal)
			{
				_position = _window.Position;
				var size = _window.ClientSize;
				if (_window.CanResize && IsValidSize(size.Width, size.Height))
				{
					_size = size;
				}
			}
			_store.Record(_name, new(_position.X, _position.Y, _visibility ? _window.IsVisible : null,
				_size?.Width, _size?.Height));
		}

		public void Fit(IReadOnlyList<ScreenArea> screens)
		{
			Capture();
			var size = _size ?? new Size(double.IsFinite(_window.Width) ? _window.Width : _window.ClientSize.Width,
				double.IsFinite(_window.Height) ? _window.Height : _window.ClientSize.Height);
			var frame = _window.FrameSize ?? _window.ClientSize;
			var decoration = new Size(Math.Max(0, frame.Width - _window.ClientSize.Width),
				Math.Max(0, frame.Height - _window.ClientSize.Height));
			var fit = WindowPlacement.Fit(_position, size, decoration, _window.DesktopScaling, screens,
				new Size(Math.Max(1, _window.MinWidth), Math.Max(1, _window.MinHeight)), _window.CanResize);
			_fitting = true;
			try
			{
				_window.WindowStartupLocation = WindowStartupLocation.Manual;
				if (_window.CanResize)
				{
					_window.Width = fit.ClientSize.Width;
					_window.Height = fit.ClientSize.Height;
					_size = fit.ClientSize;
				}
				_window.Position = fit.Position;
				_position = fit.Position;
				_store.Record(_name, new(_position.X, _position.Y, _visibility ? _window.IsVisible : null,
					_size?.Width, _size?.Height));
			}
			finally
			{
				_fitting = false;
			}
		}
		private void Closed(object? sender, EventArgs e)
		{
			Capture();
			Dispose();
			_store._tracked.Remove(_window);
		}
		public void Dispose()
		{
			_window.Opened -= Opened;
			_window.PositionChanged -= Moved;
			_window.Resized -= Resized;
			_window.PropertyChanged -= Changed;
			_window.Closed -= Closed;
		}
	}
}
