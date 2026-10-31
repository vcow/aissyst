using Avalonia.Input;
using System.Text.Json.Serialization;

namespace aissyst.desktop.Settings;

internal sealed record PromptSettings
{
	public double FontSize { get; init; } = 18;
	public string ForceDialog { get; init; } = "Ctrl+Alt+Space";
	public string Translate { get; init; } = "Ctrl+Alt+D1";
	public string Suggest { get; init; } = "Ctrl+Alt+D2";
	public string Answer { get; init; } = "Ctrl+Alt+D3";
	public string TranslateAndAnswer { get; init; } = "Ctrl+Alt+D5";
	public string TranslateAndSuggest { get; init; } = "Ctrl+Alt+D4";
	[JsonIgnore]
	public string[] Gestures => [ForceDialog, Translate, Suggest, Answer, TranslateAndAnswer, TranslateAndSuggest];

	public PromptSettings Validated()
	{
		if (!double.IsFinite(FontSize) || FontSize is < 8 or > 72)
		{
			throw new ArgumentException("Prompt font size must be between 8 and 72.");
		}
		var used = new HashSet<KeyGesture>();
		foreach (var text in Gestures)
		{
			if (string.IsNullOrWhiteSpace(text))
			{
				continue;
			}
			KeyGesture gesture;
			try { gesture = KeyGesture.Parse(text); }
			catch (Exception exception) when (exception is ArgumentException or FormatException)
			{
				throw new ArgumentException($"Invalid shortcut: {text}.", exception);
			}
			if (gesture.Key == Key.None || gesture.KeyModifiers == KeyModifiers.None
				|| gesture.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
					or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
			{
				throw new ArgumentException("Shortcuts must include a modifier and a non-modifier key.");
			}
			if (gesture.Key is Key.Q or Key.OemComma && gesture.KeyModifiers is KeyModifiers.Meta or KeyModifiers.Control)
			{
				throw new ArgumentException("The shortcuts for Quit and Settings are reserved.");
			}
			if (!used.Add(gesture))
			{
				throw new ArgumentException("Each command must have a different shortcut.");
			}
		}
		return this;
	}
}
