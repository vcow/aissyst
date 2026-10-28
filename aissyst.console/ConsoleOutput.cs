using aissyst.core.Output;
using aissyst.core.Processor;

namespace aissyst.console;

public sealed class ConsoleOutput : IOutput
{
	private readonly Lock _sync = new();
	private readonly TextWriter _writer;
	private readonly Action _clear;

	public ConsoleOutput() : this(Console.Out, () =>
	{
		if (!Console.IsOutputRedirected)
		{
			Console.Clear();
		}
	})
	{
	}

	internal ConsoleOutput(TextWriter writer, Action clear)
	{
		_writer = writer;
		_clear = clear;
	}

	public void Clear()
	{
		lock (_sync)
		{
			_clear();
		}
	}

	public void PrintMultipleDialogMessages(IReadOnlyList<DialogMessage> messages)
	{
		lock (_sync)
		{
			foreach (var message in messages)
			{
				WriteDialogMessage(message);
			}
		}
	}

	public void PrintDialogMessage(DialogMessage message)
	{
		lock (_sync)
		{
			WriteDialogMessage(message);
		}
	}

	private void WriteDialogMessage(DialogMessage message) =>
		_writer.WriteLine($"{message.Source} [{message.Timestamp:HH:mm:ss}]: {message.Text}");

	public void PrintLLMAnswer(string text)
	{
		lock (_sync)
		{
			_writer.WriteLine(text);
		}
	}
}
