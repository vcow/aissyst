using aissyst.core;

namespace aissyst.desktop.Wizard;

internal sealed class DesktopStartupProgress(WizardService wizard) : IProgress<InitializationProgress>
{
	public void Report(InitializationProgress value) =>
		wizard.ReportProgress(value.Message, value.CompletedBytes, value.TotalBytes);
}
