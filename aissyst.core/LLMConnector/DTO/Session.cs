using System.Text.Json.Serialization;

namespace aissyst.core.LLMConnector.DTO;

public sealed class Session
{
	[JsonPropertyName("preview")] public string Preview { get; set; } = string.Empty;
	[JsonPropertyName("context")] public IList<SessionContextItem> Context { get; set; } = Array.Empty<SessionContextItem>();
	[JsonPropertyName("dialog")] public IList<DialogItem> Dialog { get; set; } = new List<DialogItem>();
	[JsonPropertyName("md_format")] public bool UseMdFormat { get; set; } = false;
}