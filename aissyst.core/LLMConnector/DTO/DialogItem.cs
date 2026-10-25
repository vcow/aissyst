using System.Text.Json.Serialization;

namespace aissyst.core.LLMConnector.DTO;

public sealed class DialogItem
{
	[JsonPropertyName("ts")] public DateTime Timestamp { get; set; }
	[JsonPropertyName("role")] public SenderRole Role { get; set; } = SenderRole.Unknown;
	[JsonPropertyName("message")] public string Message { get; set; } = string.Empty;
}