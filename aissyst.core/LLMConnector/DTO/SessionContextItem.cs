using System.Text.Json.Serialization;

namespace aissyst.core.LLMConnector.DTO;

public sealed class SessionContextItem
{
	[JsonPropertyName("topic")] public string Topic { get; set; } = string.Empty;
	[JsonPropertyName("body")] public string Body { get; set; } = string.Empty;
}