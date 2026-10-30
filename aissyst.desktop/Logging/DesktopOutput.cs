using aissyst.core.Output;
using aissyst.core.Processor;
using Microsoft.Extensions.Logging;

namespace aissyst.desktop.Logging;

internal sealed class DesktopOutput(ILogger<DesktopOutput> logger) : IOutput
{
	private readonly Lock _sync = new();

	public void Clear()
	{
		// A dialog reload must not erase the system log.
	}

	public void PrintMultipleDialogMessages(IReadOnlyList<DialogMessage> messages)
	{
		lock (_sync)
		{
			foreach (var message in messages)
			{
				PrintDialogMessage(message);
			}
		}
	}

	public void PrintDialogMessage(DialogMessage message)
	{
		lock (_sync)
		{
			logger.LogInformation("{Source} [{Timestamp:HH:mm:ss}]: {Text}", message.Source, message.Timestamp, message.Text);
		}
	}

	public void PrintLLMAnswer(string text)
	{
		lock (_sync)
		{
			logger.LogInformation("{Answer}", text);
		}
	}
}
