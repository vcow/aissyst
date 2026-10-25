using aissyst.core.LLMConnector.DTO;

namespace aissyst.console;

public sealed class ConsoleHotkeySettings
{
	public char FinishRecording { get; }
	public char StopSession { get; }
	public char ClearQueue { get; }
	public char Quit { get; }
	public char Translate { get; }
	public char Advice { get; }
	public char Reply { get; }
	public char TranslateAndAdvice { get; }
	public char TranslateAndReply { get; }
	private readonly Dictionary<char, RequestType> _requests;

	public ConsoleHotkeySettings(char finishRecording = ' ', char stopSession = 'S', char clearQueue = 'C', char quit = 'Q',
		char translate = '1', char advice = '2', char reply = '3', char translateAndAdvice = '4', char translateAndReply = '5')
	{
		var keys = new[] { finishRecording, stopSession, clearQueue, quit, translate, advice, reply, translateAndAdvice, translateAndReply }
			.Select(char.ToUpperInvariant).ToArray();
		if (keys.Skip(1).Take(3).Any(key => !char.IsLetter(key))
			|| keys.Skip(4).Any(key => !char.IsLetterOrDigit(key))
			|| (!char.IsLetter(keys[0]) && keys[0] != ' ')
			|| keys.Distinct().Count() != keys.Length)
		{
			throw new ArgumentException("Hotkeys must be distinct, ignoring case. Session controls must be letters, assistance keys may be letters or digits, and the finish-recording key may also be Space.");
		}
		FinishRecording = keys[0];
		StopSession = keys[1];
		ClearQueue = keys[2];
		Quit = keys[3];
		Translate = keys[4];
		Advice = keys[5];
		Reply = keys[6];
		TranslateAndAdvice = keys[7];
		TranslateAndReply = keys[8];
		_requests = new Dictionary<char, RequestType>
		{
			[Translate] = RequestType.Translate,
			[Advice] = RequestType.Advice,
			[Reply] = RequestType.Reply,
			[TranslateAndAdvice] = RequestType.TranslateAndAdvice,
			[TranslateAndReply] = RequestType.TranslateAndReply
		};
	}

	public bool TryGetRequestType(string command, out RequestType request)
	{
		var key = command.Trim();
		if (key.Length == 1)
		{
			return _requests.TryGetValue(char.ToUpperInvariant(key[0]), out request);
		}
		request = default;
		return false;
	}
}
