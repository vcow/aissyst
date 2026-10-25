using System.Collections.Concurrent;
using aissyst.core.Output;
using aissyst.core.Processor;

namespace aissyst.tests;

internal sealed class TestOutput : IOutput
{
	internal readonly ConcurrentQueue<DialogMessage> Messages = new();
	internal readonly ConcurrentQueue<string> Answers = new();
	public void PrintDialogMessage(DialogMessage message) => Messages.Enqueue(message);
	public void PrintLLMAnswer(string text) => Answers.Enqueue(text);
}
