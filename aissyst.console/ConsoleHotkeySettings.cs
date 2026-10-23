namespace aissyst.console;

public sealed class ConsoleHotkeySettings
{
	public char FinishRecording { get; }
	public char StopSession { get; }
	public char ClearQueue { get; }
	public char Quit { get; }

	public ConsoleHotkeySettings(char finishRecording = ' ', char stopSession = 'S', char clearQueue = 'C', char quit = 'Q')
	{
		var keys = new[] { finishRecording, stopSession, clearQueue, quit }.Select(char.ToUpperInvariant).ToArray();
		if (keys.Skip(1).Any(key => !char.IsLetter(key))
			|| (!char.IsLetter(keys[0]) && keys[0] != ' ')
			|| keys.Distinct().Count() != keys.Length)
		{
			throw new ArgumentException("Hotkeys must be distinct letters, ignoring case. The finish-recording hotkey may also be Space.");
		}
		FinishRecording = keys[0];
		StopSession = keys[1];
		ClearQueue = keys[2];
		Quit = keys[3];
	}
}
