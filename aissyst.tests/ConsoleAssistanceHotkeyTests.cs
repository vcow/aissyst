using aissyst.console;
using aissyst.core.LLMConnector.DTO;

namespace aissyst.tests;

public sealed class ConsoleAssistanceHotkeyTests
{
	[Theory]
	[InlineData("1", RequestType.Translate)]
	[InlineData("2", RequestType.Advice)]
	[InlineData("3", RequestType.Reply)]
	[InlineData("4", RequestType.TranslateAndAdvice)]
	[InlineData("5", RequestType.TranslateAndReply)]
	public void DefaultKeysMapToRequestedOperations(string command, RequestType expected)
	{
		Assert.True(new ConsoleHotkeySettings().TryGetRequestType(command, out var actual));
		Assert.Equal(expected, actual);
	}

	[Fact]
	public void AssistanceKeysCanBeRemappedAndAreCaseInsensitive()
	{
		var hotkeys = new ConsoleHotkeySettings(translate: 'T', advice: 'A', reply: 'R', translateAndAdvice: 'H', translateAndReply: 'F');
		Assert.True(hotkeys.TryGetRequestType("t", out var type));
		Assert.Equal(RequestType.Translate, type);
		Assert.True(hotkeys.TryGetRequestType(" f ", out type));
		Assert.Equal(RequestType.TranslateAndReply, type);
		Assert.False(hotkeys.TryGetRequestType("1", out _));
	}

	[Theory]
	[InlineData('s')]
	[InlineData('2')]
	[InlineData(' ')]
	public void ConflictingOrUnsupportedAssistanceKeysAreRejected(char translate)
	{
		Assert.Throws<ArgumentException>(() => new ConsoleHotkeySettings(translate: translate));
	}
}
