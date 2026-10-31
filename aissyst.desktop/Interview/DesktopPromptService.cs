using Avalonia.Controls;
using Avalonia.Threading;
using aissyst.desktop.State;
using aissyst.desktop.ViewModels;
using aissyst.desktop.Views;

namespace aissyst.desktop.Interview;

internal sealed class DesktopPromptService(PromptViewModel model, InterviewCommands commands,
	DesktopWindowState state) : IAsyncDisposable
{
	private bool _closing;
	internal PromptWindow? Window { get; private set; }
	public void OpenAtStartup()
	{
		CreateWindow();
		if (state.ShouldShow("prompt")) { Show(false); }
	}
	private void CreateWindow()
	{
		if (Window != null || _closing) { return; }
		Window = new PromptWindow { DataContext = model };
		Window.Closing += (_, args) =>
		{
			if (!_closing)
			{
				args.Cancel = true;
				Window.Hide();
			}
		};
		state.Attach(Window, "prompt");
		commands.Executing += CommandExecuting;
	}
	private void CommandExecuting(InterviewCommand command)
	{
		if (command.Request != null) { Show(false); }
	}
	public void Show(bool activate = true)
	{
		if (_closing) { return; }
		CreateWindow();
		Window!.Show();
		if (Window.WindowState == WindowState.Minimized) { Window.WindowState = WindowState.Normal; }
		if (activate) { Window.Activate(); }
	}
	public async ValueTask DisposeAsync()
	{
		_closing = true;
		commands.Executing -= CommandExecuting;
		await Dispatcher.UIThread.InvokeAsync(() => Window?.Close());
	}
}
