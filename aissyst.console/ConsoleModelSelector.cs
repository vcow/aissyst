using aissyst.core.LLMConnector;

namespace aissyst.console;

public sealed class ConsoleModelSelector : IModelSelector
{
	private readonly Func<CancellationToken, Task<string?>> _readLine;
	private readonly TextWriter _output;
	private LLMModel? _selected;

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
				_selected = models[number - 1];
				return;
			}
			_output.WriteLine($"Invalid selection. Enter a number from 1 to {models.Count}.");
		}
	}
}
