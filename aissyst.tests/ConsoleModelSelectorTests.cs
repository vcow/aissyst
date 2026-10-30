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
		Assert.Throws<InvalidOperationException>(() => selector.GetSelectedReasoningEffort());
		LLMModel[] models = [new("first", "First model"), new("second", "Second model")];
		await selector.InitializeAsync(models);
		Assert.Same(models[1], selector.GetSelectedModel());
		Assert.Null(selector.GetSelectedReasoningEffort());
		Assert.Contains("First model", output.ToString());
		Assert.Contains("Invalid selection", output.ToString());
	}

	[Theory]
	[InlineData("1", "low")]
	[InlineData("2", "medium")]
	[InlineData("0", null)]
	[InlineData("", "medium")]
	public async Task SelectsOnlyAdvertisedReasoningLevelsAndSupportsCatalogOrServerDefault(string choice, string? expected)
	{
		var input = new Queue<string>(["1", "bad", "-1", "3", choice]);
		using var output = new StringWriter();
		var selector = new ConsoleModelSelector(_ => Task.FromResult<string?>(input.Dequeue()), output);
		var model = new LLMModel("model", "Model")
		{
			ReasoningLevels = [new("low", "Fast responses"), new("medium", "Balanced responses")],
			DefaultReasoningEffort = "medium"
		};
		await selector.InitializeAsync([model]);
		Assert.Same(model, selector.GetSelectedModel());
		Assert.Equal(expected, selector.GetSelectedReasoningEffort());
		Assert.Contains("Available reasoning levels for Model", output.ToString());
		Assert.Contains("Fast responses", output.ToString());
		Assert.Contains("medium (catalog default)", output.ToString());
		Assert.DoesNotContain("high -", output.ToString());
		Assert.Empty(input);
	}

	[Fact]
	public async Task CatalogCanAdvertiseNoneAndUnknownDefaultFallsBackToServerDefault()
	{
		var input = new Queue<string>(["1", "1", "1", ""]);
		using var output = new StringWriter();
		var selector = new ConsoleModelSelector(_ => Task.FromResult<string?>(input.Dequeue()), output);
		var model = new LLMModel("model", "Model") { ReasoningLevels = [new("none", "No reasoning")], DefaultReasoningEffort = "unknown" };
		await selector.InitializeAsync([model]);
		Assert.Equal("none", selector.GetSelectedReasoningEffort());
		await selector.InitializeAsync([model]);
		Assert.Null(selector.GetSelectedReasoningEffort());
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task EndOfInputOrCancellationWhileSelectingReasoningDoesNotCommitModel(bool cancel)
	{
		using var cancellation = new CancellationTokenSource();
		var reads = 0;
		var selector = new ConsoleModelSelector(_ =>
		{
			if (++reads == 1)
			{
				return Task.FromResult<string?>("1");
			}
			if (cancel)
			{
				cancellation.Cancel();
				return Task.FromResult<string?>("1");
			}
			return Task.FromResult<string?>(null);
		}, new StringWriter());
		var model = new LLMModel("model", "Model") { ReasoningLevels = [new("low", "Fast responses")] };
		if (cancel)
		{
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => selector.InitializeAsync([model], cancellation.Token));
		}
		else
		{
			await Assert.ThrowsAsync<EndOfStreamException>(() => selector.InitializeAsync([model], cancellation.Token));
		}
		Assert.Throws<InvalidOperationException>(() => selector.GetSelectedModel());
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
