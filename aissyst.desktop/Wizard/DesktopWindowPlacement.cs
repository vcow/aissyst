using Avalonia;
using Avalonia.Controls;

namespace aissyst.desktop.Wizard;

internal static class DesktopWindowPlacement
{
	public static void Show(Window log, Window wizard)
	{
		var screen = log.Screens.ScreenFromWindow(log) ?? log.Screens.Primary;
		wizard.WindowStartupLocation = WindowStartupLocation.Manual;
		if (screen != null)
		{
			var area = screen.WorkingArea;
			var scale = log.RenderScaling;
			const double gap = 16;
			var initialLogWidth = double.IsFinite(log.Width) ? log.Width : Math.Max(320, log.Bounds.Width);
			var logHeight = double.IsFinite(log.Height) ? log.Height : Math.Max(200, log.Bounds.Height);
			var availableWidth = area.Width / scale;
			log.Width = Math.Min(initialLogWidth, Math.Max(log.MinWidth, availableWidth - wizard.Width - gap));
			var logWidth = (int)Math.Ceiling(log.Width * scale);
			var wizardWidth = (int)Math.Ceiling(wizard.Width * scale);
			var spacing = (int)Math.Ceiling(gap * scale);
			var height = (int)Math.Ceiling(Math.Max(logHeight, wizard.Height) * scale);
			var x = Math.Clamp(log.Position.X, area.X, Math.Max(area.X, area.Right - logWidth - spacing - wizardWidth));
			var y = Math.Clamp(log.Position.Y, area.Y, Math.Max(area.Y, area.Bottom - height));
			log.Position = new PixelPoint(x, y);
			wizard.Position = new PixelPoint(Math.Min(x + logWidth + spacing, Math.Max(area.X, area.Right - wizardWidth)), y);
		}
		wizard.Show();
	}
}
