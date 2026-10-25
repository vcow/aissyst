using System.Text.Json.Serialization;
using aissyst.core.Serialization;

namespace aissyst.core.LLMConnector.DTO;

public sealed class DialogResponse
{
	[JsonPropertyName("request_id")] public int RequestId { get; set; }
	[JsonPropertyName("error")] public string Error { get; set; } = string.Empty;
	[JsonPropertyName("summary")] public string Summary { get; set; } = string.Empty;
	[JsonPropertyName("summary_ts")]
	[JsonConverter(typeof(NullableDateTimeJsonConverter))]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public DateTime? SummaryTimestamp { get; set; }
	[JsonPropertyName("message")] public string Message { get; set; } = string.Empty;
}
