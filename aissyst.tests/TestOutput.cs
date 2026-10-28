using System.Collections.Concurrent;
using aissyst.core.Output;
using aissyst.core.Processor;

namespace aissyst.tests;

internal sealed class TestOutput : IOutput
{
	private readonly Lock _sync = new();
	internal readonly ConcurrentQueue<DialogMessage> Messages = new();
	internal readonly ConcurrentQueue<string> Answers = new();
	internal int ClearCalls;
	internal int BatchCalls;

	public void Clear()
	{
		lock (_sync)
		{
			Messages.Clear();
			Answers.Clear();
			ClearCalls++;
		}
	}

	public void PrintMultipleDialogMessages(IReadOnlyList<DialogMessage> messages)
	{
		lock (_sync)
		{
			foreach (var message in messages)
			{
				Messages.Enqueue(message);
			}
			BatchCalls++;
		}
	}

	public void PrintDialogMessage(DialogMessage message)
	{
		lock (_sync)
		{
			Messages.Enqueue(message);
		}
	}

	public void PrintLLMAnswer(string text)
	{
		lock (_sync)
		{
			Answers.Enqueue(text);
		}
	}
}
