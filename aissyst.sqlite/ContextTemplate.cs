using System.IO.Compression;
using System.Text;
using aissyst.core.Database;

namespace aissyst.sqlite;

internal sealed record ContextTemplate(string Preview, string Instructions, ContextRecord[] Context)
{
	public static ContextTemplate Read(string filePath, CancellationToken cancellationToken)
	{
		using var archive = ZipFile.OpenRead(filePath);
		string ReadText(ZipArchiveEntry entry)
		{
			cancellationToken.ThrowIfCancellationRequested();
			using var stream = entry.Open();
			using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false);
			return reader.ReadToEnd().TrimStart('\uFEFF');
		}

		string ReadRequired(string name)
		{
			var entries = archive.Entries.Where(entry => entry.FullName == name).ToArray();
			if (entries.Length != 1)
			{
				throw new InvalidDataException($"The template must contain exactly one root file named '{name}'.");
			}
			return ReadText(entries[0]);
		}

		var preview = ReadRequired("preview.txt");
		var instructions = ReadRequired("agent_initial_instructions.txt");
		var blocks = new List<ContextRecord>();
		foreach (var entry in archive.Entries)
		{
			if (!entry.FullName.StartsWith("context/", StringComparison.Ordinal))
			{
				continue;
			}
			var name = entry.FullName["context/".Length..];
			if (name.Length == 0 || name.Contains('/') || name.Contains('\\')
				|| !name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			var text = ReadText(entry);
			var end = text.IndexOfAny(['\r', '\n']);
			var topic = (end < 0 ? text : text[..end]).Trim();
			if (topic.Length == 0)
			{
				throw new InvalidDataException($"The context block '{entry.FullName}' has an empty title.");
			}
			var bodyStart = end < 0 ? text.Length : end + 1;
			if (end >= 0 && text[end] == '\r' && bodyStart < text.Length && text[bodyStart] == '\n')
			{
				bodyStart++;
			}
			blocks.Add(new ContextRecord(topic, text[bodyStart..]));
		}
		if (!archive.Entries.Any(entry => entry.FullName.StartsWith("context/", StringComparison.Ordinal)))
		{
			throw new InvalidDataException("The template must contain a root context directory.");
		}
		cancellationToken.ThrowIfCancellationRequested();
		return new ContextTemplate(preview, instructions, blocks.ToArray());
	}
}
