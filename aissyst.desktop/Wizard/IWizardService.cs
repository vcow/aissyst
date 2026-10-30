namespace aissyst.desktop.Wizard;

internal sealed record WizardAction(string Key, string Label, string Confirmation)
{
	public string ConfirmationLabel { get; init; } = "Confirm";
}

internal sealed record WizardOption(string Key, string Title, string Description = "")
{
	public string NextLabel { get; init; } = "Next";
	public WizardAction? SecondaryAction { get; init; }
}

internal sealed record WizardPage(string Title, string Description, IReadOnlyList<WizardOption> Options)
{
	public string? SelectedKey { get; init; }
	public bool IsTemplatePath { get; init; }
	public string Path { get; init; } = string.Empty;
	public string Error { get; init; } = string.Empty;
}

internal interface IWizardService
{
	Task<string> ShowAsync(WizardPage page, CancellationToken cancellationToken = default);
}
