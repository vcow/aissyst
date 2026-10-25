using aissyst.core.Output;
using aissyst.core.Processor;

namespace aissyst.console;

public sealed class ConsoleOutput : IOutput
{
	public void PrintDialogMessage(DialogMessage message)
	{
		Console.WriteLine($"{message.Source} [{message.Timestamp:HH:mm:ss}]: {message.Text}");
	}

	public void PrintLLMAnswer(string text)
	{
		Console.WriteLine(text);
	}
}
