using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace aissyst.desktop.Views;

internal sealed class ConfirmationWindow : Window
{
	public ConfirmationWindow()
	{
		Title = "Reconnect";
		Topmost = true;
		CanResize = false;
		CanMinimize = false;
		CanMaximize = false;
		ShowInTaskbar = false;
		Width = 340;
		Height = 150;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;
		var yes = new Button { Name = "YesButton", Content = "Yes", MinWidth = 80 };
		var no = new Button { Name = "NoButton", Content = "No", MinWidth = 80, IsDefault = true, IsCancel = true };
		yes.Click += (_, _) => Close(true);
		no.Click += (_, _) => Close(false);
		Content = new StackPanel
		{
			Margin = new Thickness(24), Spacing = 24,
			Children =
			{
				new TextBlock { Text = "Are you sure?", FontSize = 16 },
				new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
					Spacing = 12, Children = { yes, no } }
			}
		};
	}
}
