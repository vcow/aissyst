using aissyst.core.LLMConnector;

namespace aissyst.console;

public sealed class ConsoleModelSelector : IModelSelector
{
	private readonly Func<CancellationToken, Task<string?>> _readLine;
	private readonly TextWriter _output;
	private LLMModel? _selected;
	private string? _reasoningEffort;

	public ConsoleModelSelector() : this(ConsoleInput.ReadLineAsync, Console.Out)
	{
	}

	internal ConsoleModelSelector(Func<CancellationToken, Task<string?>> readLine, TextWriter output)
	{
		_readLine = readLine;
		_output = output;
	}

	public LLMModel GetSelectedModel() => _selected
		?? throw new InvalidOperationException("No model has been selected.");

	public string? GetSelectedReasoningEffort()
	{
		_ = GetSelectedModel();
		return _reasoningEffort;
	}

	public async Task InitializeAsync(IReadOnlyList<LLMModel> models, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (models.Count == 0)
		{
			throw new InvalidOperationException("No models are available for this ChatGPT account.");
		}
		_output.WriteLine("Available models:");
		for (var index = 0; index < models.Count; index++)
		{
			_output.WriteLine($"{index + 1}. {models[index].Name}");
		}
		while (true)
		{
			_output.Write($"Select model by number (1-{models.Count}): ");
			var input = await _readLine(cancellationToken)
				?? throw new EndOfStreamException("Standard input was closed before selecting a model.");
			cancellationToken.ThrowIfCancellationRequested();
			if (int.TryParse(input, out var number) && number >= 1 && number <= models.Count)
			{
				var model = models[number - 1];
				var effort = await SelectReasoningEffortAsync(model, cancellationToken);
				_reasoningEffort = effort;
				_selected = model;
				return;
			}
			_output.WriteLine($"Invalid selection. Enter a number from 1 to {models.Count}.");
		}
	}

	private async Task<string?> SelectReasoningEffortAsync(LLMModel model, CancellationToken cancellationToken)
	{
		if (model.ReasoningLevels.Count == 0)
		{
			_output.WriteLine("Reasoning effort: server default (no selectable levels in the model catalog).");
			return null;
		}
		_output.WriteLine($"Available reasoning levels for {model.Name}:");
		_output.WriteLine("0. Server default");
		var defaultNumber = 0;
		for (var index = 0; index < model.ReasoningLevels.Count; index++)
		{
			var level = model.ReasoningLevels[index];
			var isDefault = level.Effort == model.DefaultReasoningEffort;
			if (isDefault)
			{
				defaultNumber = index + 1;
			}
			_output.WriteLine($"{index + 1}. {level.Effort}{(isDefault ? " (catalog default)" : "")} - {level.Description}");
		}
		while (true)
		{
			_output.Write($"Select reasoning level by number (0-{model.ReasoningLevels.Count}, Enter for {defaultNumber}): ");
			var input = await _readLine(cancellationToken)
				?? throw new EndOfStreamException("Standard input was closed before selecting a reasoning level.");
			cancellationToken.ThrowIfCancellationRequested();
			var number = defaultNumber;
			if (string.IsNullOrWhiteSpace(input) || (int.TryParse(input, out number) && number >= 0 && number <= model.ReasoningLevels.Count))
			{
				var effort = number == 0 ? null : model.ReasoningLevels[number - 1].Effort;
				_output.WriteLine($"Reasoning effort: {effort ?? "server default"}.");
				return effort;
			}
			_output.WriteLine($"Invalid selection. Enter a number from 0 to {model.ReasoningLevels.Count}.");
		}
	}
}
