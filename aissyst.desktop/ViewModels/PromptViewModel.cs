using Avalonia.Threading;
using aissyst.desktop.Interview;
using aissyst.desktop.Settings;

namespace aissyst.desktop.ViewModels;

internal sealed class PromptViewModel
	: ViewModel
{
	public string Markdown { get; private set; } = string.Empty;
	public double FontSize { get; private set; } = 18;
	public bool IsProcessing { get; private set; }
	public IReadOnlyList<InterviewCommand> Commands { get; private set; } = [];
	public event Action? AnswerChanged;

	public void SetCommands(IReadOnlyList<InterviewCommand> commands)
	{
		Commands = commands;
		Notify(nameof(Commands));
	}

	public void Apply(PromptSettings settings)
	{
		FontSize = settings.FontSize;
		Notify(nameof(FontSize));
	}

	public void ShowAnswer(string text) => OnUI(() =>
	{
		Markdown = text;
		Notify(nameof(Markdown));
		AnswerChanged?.Invoke();
	});

	public void SetProcessing(bool processing)
	{
		IsProcessing = processing;
		Notify(nameof(IsProcessing));
	}

	internal static void OnUI(Action action)
	{
		if (Dispatcher.UIThread.CheckAccess()) { action(); }
		else { Dispatcher.UIThread.Post(action); }
	}
}
