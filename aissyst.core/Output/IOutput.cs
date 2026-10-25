using aissyst.core.Processor;

namespace aissyst.core.Output;

public interface IOutput
{
	void PrintDialogMessage(DialogMessage message);
	void PrintLLMAnswer(string text);
}