using Avalonia;

namespace aissyst.desktop.State;

internal sealed record ScreenArea(PixelRect WorkingArea, double Scaling);
internal readonly record struct WindowPlacement(PixelPoint Position, Size ClientSize)
{
	public static WindowPlacement Fit(PixelPoint position, Size clientSize, Size decorationSize, double scaling,
		IReadOnlyList<ScreenArea> screens, Size minimumSize, bool canResize)
	{
		if (screens.Count == 0)
		{
			return new(position, clientSize);
		}
		var frame = new PixelRect(position, PixelSize.FromSize(clientSize + decorationSize, scaling));
		var target = screens.OrderByDescending(screen => Intersection(frame, screen.WorkingArea))
			.ThenBy(screen => Distance(frame, screen.WorkingArea)).First();
		var area = target.WorkingArea;
		var targetScale = target.Scaling;
		var width = canResize ? Math.Max(minimumSize.Width, Math.Min(clientSize.Width,
			area.Width / targetScale - decorationSize.Width)) : clientSize.Width;
		var height = canResize ? Math.Max(minimumSize.Height, Math.Min(clientSize.Height,
			area.Height / targetScale - decorationSize.Height)) : clientSize.Height;
		var size = new Size(width, height);
		var frameSize = PixelSize.FromSize(size + decorationSize, targetScale);
		return new(new PixelPoint(Math.Clamp(position.X, area.X, Math.Max(area.X, area.Right - frameSize.Width)),
			Math.Clamp(position.Y, area.Y, Math.Max(area.Y, area.Bottom - frameSize.Height))), size);
	}

	private static double Intersection(PixelRect a, PixelRect b) =>
		Math.Max(0d, (double)Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X))
		* Math.Max(0d, (double)Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y));

	private static double Distance(PixelRect a, PixelRect b)
	{
		var x = Math.Max(0d, Math.Max((double)b.X - a.Right, (double)a.X - b.Right));
		var y = Math.Max(0d, Math.Max((double)b.Y - a.Bottom, (double)a.Y - b.Bottom));
		return x * x + y * y;
	}
}
