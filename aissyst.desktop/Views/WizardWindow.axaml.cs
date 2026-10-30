using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;

namespace aissyst.desktop.Views;

public partial class WizardWindow : Window
{
	private bool _allowClose;
	internal event Action? NextRequested;
	internal event Action? SecondaryRequested;
	internal event Action? BrowseRequested;

	public WizardWindow() => InitializeComponent();

	protected override void OnClosing(WindowClosingEventArgs args)
	{
		base.OnClosing(args);
		if (!_allowClose)
		{
			args.Cancel = true;
		}
	}

	internal void Complete()
	{
		_allowClose = true;
		Close();
	}

	private void TitlePointerPressed(object? sender, PointerPressedEventArgs args)
	{
		if (args.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
		{
			BeginMoveDrag(args);
		}
	}

	private void NextClicked(object? sender, RoutedEventArgs args) => NextRequested?.Invoke();
	private void SecondaryClicked(object? sender, RoutedEventArgs args) => SecondaryRequested?.Invoke();
	private void BrowseClicked(object? sender, RoutedEventArgs args) => BrowseRequested?.Invoke();
}
