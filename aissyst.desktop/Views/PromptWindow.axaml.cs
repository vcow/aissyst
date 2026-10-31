using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using aissyst.desktop.ViewModels;
using Markdown.Avalonia;

namespace aissyst.desktop.Views;

public partial class PromptWindow : Window
{
	private PromptViewModel? _model;
	internal MarkdownScrollViewer Viewer { get; }
	public PromptWindow()
	{
		InitializeComponent();
		Viewer = this.FindControl<MarkdownScrollViewer>("PromptMarkdown")!;
		DataContextChanged += (_, _) =>
		{
			Detach();
			_model = DataContext as PromptViewModel;
			if (_model != null)
			{
				_model.AnswerChanged += ResetScroll;
				_model.PropertyChanged += ModelChanged;
				ApplyFont();
			}
		};
		Opened += (_, _) => WindowStartupLocation = WindowStartupLocation.Manual;
		Closed += (_, _) => Detach();
	}
	private void Detach()
	{
		if (_model != null)
		{
			_model.AnswerChanged -= ResetScroll;
			_model.PropertyChanged -= ModelChanged;
		}
	}
	private void ModelChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(PromptViewModel.FontSize)) { ApplyFont(); }
	}
	private void ApplyFont()
	{
		// The Fluent Markdown styles derive paragraph and heading sizes from this resource.
		Viewer.Resources["ControlContentThemeFontSize"] = _model?.FontSize ?? 18;
	}
	private void ResetScroll()
	{
		Viewer.ScrollValue = Vector.Zero;
		Dispatcher.UIThread.Post(() => Viewer.ScrollValue = Vector.Zero, DispatcherPriority.Loaded);
	}
}
