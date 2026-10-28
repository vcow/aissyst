using aissyst.core.Processor;

namespace aissyst.core.Output;

// Implementations serialize calls so a dialog batch cannot interleave with individual messages or answers.
public interface IOutput
{
	void Clear();
	void PrintMultipleDialogMessages(IReadOnlyList<DialogMessage> messages);
	void PrintDialogMessage(DialogMessage message);
	void PrintLLMAnswer(string text);
}
