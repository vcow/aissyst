using aissyst.core.Output;
using aissyst.core.Processor;
using aissyst.desktop.ViewModels;
using Microsoft.Extensions.Logging;

namespace aissyst.desktop.Logging;

internal sealed class DesktopOutput : IOutput, IDisposable
{
	private readonly Lock _sync = new();
	private readonly DialogCoordinator _dialog;
	private readonly DialogViewModel _view;
	private readonly ILogger<DesktopOutput> _logger;

	public DesktopOutput(DialogCoordinator dialog, DialogViewModel view, ILogger<DesktopOutput> logger)
	{
		_dialog = dialog;
		_view = view;
		_logger = logger;
		dialog.DialogUpdated += view.ReplaceHistory;
	}

	// Console output receives append-only fragments; this view renders the authoritative history.
	public void Clear() => RefreshDialog();
	public void PrintMultipleDialogMessages(IReadOnlyList<DialogMessage> messages) => RefreshDialog();
	public void PrintDialogMessage(DialogMessage message) => RefreshDialog();

	private void RefreshDialog()
	{
		lock (_sync)
		{
			_view.ReplaceHistory(_dialog.GetDialog(DateTime.MinValue));
		}
	}

	public void PrintLLMAnswer(string text)
	{
		lock (_sync)
		{
			_logger.LogInformation("{Answer}", text);
		}
	}

	public void Dispose() => _dialog.DialogUpdated -= _view.ReplaceHistory;
}
