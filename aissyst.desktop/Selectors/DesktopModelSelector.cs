using aissyst.core.LLMConnector;
using aissyst.desktop.Wizard;
using Microsoft.Extensions.Logging;

namespace aissyst.desktop.Selectors;

internal sealed class DesktopModelSelector(IWizardService wizard, ILogger<DesktopModelSelector> logger) : IModelSelector
{
	private LLMModel? _selected;
	private string? _effort;
	public LLMModel GetSelectedModel() => _selected ?? throw new InvalidOperationException("No model has been selected.");
	public string? GetSelectedReasoningEffort()
	{
		_ = GetSelectedModel();
		return _effort;
	}

	public async Task InitializeAsync(IReadOnlyList<LLMModel> models, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (models.Count == 0)
		{
			throw new InvalidOperationException("No models are available for this ChatGPT account.");
		}
		var selected = await wizard.ShowAsync(new WizardPage("Select a model",
			"Choose the model for translation and interview assistance. Available models depend on your account.",
			models.Select(model => new WizardOption(model.Id, model.Name)).ToArray()), cancellationToken);
		var model = models.First(item => item.Id == selected);
		string? effort = null;
		if (model.ReasoningLevels.Count > 0)
		{
			var options = new[] { new WizardOption("server", "Server default", "Let the server choose the reasoning effort.") }
				.Concat(model.ReasoningLevels.Select(level => new WizardOption(level.Effort,
					level.Effort + (level.Effort == model.DefaultReasoningEffort ? " (catalog default)" : ""), level.Description)))
				.ToArray();
			var level = await wizard.ShowAsync(new WizardPage("Select reasoning effort",
				$"Choose the reasoning effort for {model.Name}.", options)
			{
				SelectedKey = options.Any(item => item.Key == model.DefaultReasoningEffort) ? model.DefaultReasoningEffort : "server"
			}, cancellationToken);
			effort = level == "server" ? null : model.ReasoningLevels.First(item => item.Effort == level).Effort;
		}
		cancellationToken.ThrowIfCancellationRequested();
		_selected = model;
		_effort = effort;
		logger.LogInformation("Selected model: {Model}; reasoning effort: {Effort}.", model.Name, effort ?? "server default");
	}
}
