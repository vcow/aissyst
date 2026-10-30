using Avalonia.Controls;
using aissyst.desktop.Views;
using Microsoft.Extensions.Logging;

namespace aissyst.desktop.Settings;

internal sealed class DesktopSettingsDialog(DesktopSettingsStore store, Func<Window> owner,
	Func<IEnumerable<Window>> windows, ILogger<SettingsWindow> logger)
{
	internal SettingsWindow? Window { get; private set; }
	private Task? _pending;

	public Task ShowAsync()
	{
		if (Window != null)
		{
			Window.Activate();
			return _pending ?? Task.CompletedTask;
		}
		return _pending = ShowCoreAsync();
	}

	private async Task ShowCoreAsync()
	{
		var main = owner();
		if (!main.IsVisible)
		{
			main.Show();
		}
		var disabled = new HashSet<Window>();
		Window = new SettingsWindow(store, logger);
		void Disable(Window window)
		{
			if (window != Window && window.IsEnabled)
			{
				disabled.Add(window);
				window.IsEnabled = false;
			}
		}
		using var opened = Avalonia.Controls.Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) => Disable(window));
		try
		{
			foreach (var window in windows().Append(main).Distinct())
			{
				Disable(window);
			}
			await Window.ShowDialog(main);
		}
		finally
		{
			foreach (var window in disabled)
			{
				window.IsEnabled = true;
			}
			Window = null;
		}
	}

	public Task WaitForCloseAsync() => _pending ?? Task.CompletedTask;
	public void Close() => Window?.RequestClose();
}
