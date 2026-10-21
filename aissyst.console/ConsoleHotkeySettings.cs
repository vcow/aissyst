namespace aissyst.console;

public sealed class ConsoleHotkeySettings
{
	public char FinishRecording { get; }
	public char StopSession { get; }
	public char ClearQueue { get; }
	public char Quit { get; }

	public ConsoleHotkeySettings(char finishRecording = 'R', char stopSession = 'S', char clearQueue = 'C', char quit = 'Q')
	{
		var keys = new[] { finishRecording, stopSession, clearQueue, quit }.Select(char.ToUpperInvariant).ToArray();
		if (keys.Any(key => !char.IsLetter(key)) || keys.Distinct().Count() != keys.Length)
		{
			throw new ArgumentException("Hotkeys must be distinct letters, ignoring case.");
		}
		FinishRecording = keys[0];
		StopSession = keys[1];
		ClearQueue = keys[2];
		Quit = keys[3];
	}
}
