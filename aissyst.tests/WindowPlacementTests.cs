using Avalonia;
using aissyst.desktop.State;

namespace aissyst.tests;

public sealed class WindowPlacementTests
{
	[Fact]
	public void VisibleWindowKeepsItsSizeAndPositionIncludingNegativeScreenCoordinates()
	{
		var position = new PixelPoint(-1800, 100);
		var size = new Size(700, 400);
		var fit = WindowPlacement.Fit(position, size, new Size(0, 30), 1,
			[new(new PixelRect(-1920, 25, 1920, 1055), 1), new(new PixelRect(0, 25, 1920, 1055), 1)], new Size(320, 200), true);
		Assert.Equal(new WindowPlacement(position, size), fit);
	}

	[Fact]
	public void RemovedMonitorMovesWindowToNearestWorkingAreaAndIncludesTitleBar()
	{
		var fit = WindowPlacement.Fit(new PixelPoint(2500, 900), new Size(800, 450), new Size(0, 30), 1,
			[new(new PixelRect(0, 25, 1920, 1000), 1)], new Size(320, 200), true);
		Assert.Equal(new PixelPoint(1120, 545), fit.Position);
		Assert.Equal(new Size(800, 450), fit.ClientSize);
	}

	[Fact]
	public void ScreenWithMostOfTheWindowIsPreferredAndOversizedWindowShrinks()
	{
		var fit = WindowPlacement.Fit(new PixelPoint(700, 0), new Size(1400, 1000), new Size(10, 30), 1,
			[new(new PixelRect(0, 25, 800, 600), 1), new(new PixelRect(800, 25, 1200, 800), 1)], new Size(320, 200), true);
		Assert.Equal(new PixelPoint(800, 25), fit.Position);
		Assert.Equal(new Size(1190, 770), fit.ClientSize);
	}

	[Fact]
	public void MonitorScalingIsAppliedToClientSizeAndFrame()
	{
		var fit = WindowPlacement.Fit(new PixelPoint(5000, 5000), new Size(800, 600), new Size(0, 30), 2,
			[new(new PixelRect(0, 50, 1200, 900), 2)], new Size(320, 200), true);
		Assert.Equal(new PixelPoint(0, 50), fit.Position);
		Assert.Equal(new Size(600, 420), fit.ClientSize);
	}

	[Fact]
	public void FixedSizeWindowAndMinimumSizeArePreservedWhenScreenIsTooSmall()
	{
		ScreenArea[] screens = [new(new PixelRect(0, 25, 500, 400), 1)];
		var fixedFit = WindowPlacement.Fit(new PixelPoint(-1000, -1000), new Size(620, 520), new Size(0, 0), 1,
			screens, new Size(620, 520), false);
		Assert.Equal(new WindowPlacement(new PixelPoint(0, 25), new Size(620, 520)), fixedFit);
		var minimumFit = WindowPlacement.Fit(new PixelPoint(1000, 1000), new Size(1200, 900), new Size(0, 0), 1,
			screens, new Size(600, 450), true);
		Assert.Equal(new Size(600, 450), minimumFit.ClientSize);
		Assert.Equal(new PixelPoint(0, 25), minimumFit.Position);
	}
}
