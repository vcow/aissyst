using System.Text;

namespace aissyst.console;

internal static class ConsoleInput
{
	internal static async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (Console.IsInputRedirected)
		{
			// IDE consoles may block even on ReadLineAsync; do not block host shutdown with that read.
			return await Task.Run(Console.ReadLine, CancellationToken.None).WaitAsync(cancellationToken);
		}

		var input = new StringBuilder();
		while (true)
		{
			var key = await ReadKeyAsync(cancellationToken);
			if (key.Key == ConsoleKey.Enter)
			{
				Console.WriteLine();
				return input.ToString();
			}

			if (key.Key == ConsoleKey.Backspace)
			{
				if (input.Length > 0)
				{
					input.Length--;
					Console.Write("\b \b");
				}
			}
			else if (!char.IsControl(key.KeyChar))
			{
				input.Append(key.KeyChar);
				Console.Write(key.KeyChar);
			}
		}
	}

	internal static async Task<string?> ReadCommandAsync(CancellationToken cancellationToken)
	{
		if (Console.IsInputRedirected)
		{
			return await ReadLineAsync(cancellationToken);
		}

		var key = await ReadKeyAsync(cancellationToken);
		Console.WriteLine(key.KeyChar);
		return key.KeyChar.ToString();
	}

	private static async Task<ConsoleKeyInfo> ReadKeyAsync(CancellationToken cancellationToken)
	{
		while (true)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (Console.KeyAvailable)
			{
				return Console.ReadKey(intercept: true);
			}

			await Task.Delay(50, cancellationToken);
		}
	}
}
