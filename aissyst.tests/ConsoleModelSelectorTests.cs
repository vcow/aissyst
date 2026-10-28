using aissyst.console;
using aissyst.core.LLMConnector;

namespace aissyst.tests;

public sealed class ConsoleModelSelectorTests
{
	[Fact]
	public async Task RetriesInvalidInputAndReturnsSelectedCatalogEntry()
	{
		var input = new Queue<string>(["bad", "0", "3", "2"]);
		using var output = new StringWriter();
		var selector = new ConsoleModelSelector(_ => Task.FromResult<string?>(input.Dequeue()), output);
		Assert.Throws<InvalidOperationException>(() => selector.GetSelectedModel());
		LLMModel[] models = [new("first", "First model"), new("second", "Second model")];
		await selector.InitializeAsync(models);
		Assert.Same(models[1], selector.GetSelectedModel());
		Assert.Contains("First model", output.ToString());
		Assert.Contains("Invalid selection", output.ToString());
	}

	[Fact]
	public async Task EmptyCatalogAndEndOfInputDoNotSelectModel()
	{
		using var output = new StringWriter();
		var selector = new ConsoleModelSelector(_ => Task.FromResult<string?>(null), output);
		await Assert.ThrowsAsync<InvalidOperationException>(() => selector.InitializeAsync([]));
		await Assert.ThrowsAsync<EndOfStreamException>(() => selector.InitializeAsync([new("model", "Model")]));
		Assert.Throws<InvalidOperationException>(() => selector.GetSelectedModel());
	}
}
