using Avalonia.Controls;
using Avalonia.Input;

namespace aissyst.desktop.Views;

public sealed class HotkeyTextBox : TextBox
{
	protected override Type StyleKeyOverride => typeof(TextBox);

	public HotkeyTextBox()
	{
		IsReadOnly = true;
		PlaceholderText = "No shortcut";
		ToolTip.SetTip(this, "Press a key combination. Backspace or Delete clears the shortcut.");
	}
	protected override void OnKeyDown(KeyEventArgs e)
	{
		if (e.Key is Key.Tab or Key.Escape) { base.OnKeyDown(e); return; }
		e.Handled = true;
		if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
			or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) { return; }
		if (e.KeyModifiers == KeyModifiers.None && e.Key is Key.Back or Key.Delete)
		{
			SetCurrentValue(TextProperty, string.Empty);
			return;
		}
		SetCurrentValue(TextProperty, new KeyGesture(e.Key, e.KeyModifiers).ToString());
	}
}
