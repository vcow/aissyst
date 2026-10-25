using System.Text.Json.Serialization;

namespace aissyst.core.LLMConnector.DTO;

public sealed class DialogRequest
{
	[JsonPropertyName("id")] public int Id { get; set; }
	[JsonPropertyName("summary")] public string Summary { get; set; } = string.Empty;
	[JsonPropertyName("raw_records")] public IList<DialogItem> RawRecords { get; set; } = Array.Empty<DialogItem>();
	[JsonPropertyName("request")] public RequestType Request { get; set; } = RequestType.Summary;
}