using System.Text;

namespace aissyst.core.Processor;

public static class DialogMessageExtensions
{
	public static IReadOnlyList<DialogMessage> ToDialog(this IEnumerable<DialogMessage> messages, DateTime fromTime)
	{
		var result = new List<DialogMessage>();
		var text = new StringBuilder();
		DialogMessage? current = null;
		foreach (var message in messages.Where(message => message.Timestamp > fromTime
			&& !string.IsNullOrWhiteSpace(message.Text)).OrderBy(message => message.Timestamp))
		{
			if (current != null && current.Source != message.Source)
			{
				result.Add(current with { Text = text.ToString() });
				text.Clear();
			}
			if (text.Length != 0)
			{
				text.Append(' ');
			}
			text.Append(message.Text.Trim());
			// The timestamp covers every fragment included in the merged message.
			current = message;
		}
		if (current != null)
		{
			result.Add(current with { Text = text.ToString() });
		}
		return result.AsReadOnly();
	}
}
