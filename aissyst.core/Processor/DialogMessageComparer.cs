namespace aissyst.core.Processor;

internal class DialogMessageComparer : IComparer<DialogMessage>
{
	public int Compare(DialogMessage? x, DialogMessage? y)
	{
		if (!ReferenceEquals(x, y))
		{
			if (x == null) return 1;
			if (y == null) return -1;
			if (x.Timestamp > y.Timestamp) return 1;
			if (x.Timestamp < y.Timestamp) return -1;
		}

		return 0;
	}
}