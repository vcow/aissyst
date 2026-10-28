using aissyst.core.Database;

namespace aissyst.sqlite;

internal sealed class SessionDocument
{
	public required int FormatVersion { get; init; }
	public required string Name { get; init; }
	public required DateTime CreationTime { get; init; }
	public required string Instructions { get; init; }
	public required string Preview { get; init; }
	public required ContextRecord[] Context { get; init; }
	public required DialogRecord[] Dialog { get; init; }

	public void Validate()
	{
		if (FormatVersion != 1)
		{
			throw new InvalidDataException($"Unsupported session export version: {FormatVersion}.");
		}
		if (Name == null || Instructions == null || Preview == null || Context == null || Dialog == null
			|| Context.Any(block => block == null || block.Topic == null || block.Body == null)
			|| Dialog.Any(record => record == null || record.Role == null || record.Message == null))
		{
			throw new InvalidDataException("The session export contains null required values.");
		}
	}
}
