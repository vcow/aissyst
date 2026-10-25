using System.Text.Json.Serialization;

namespace aissyst.core.LLMConnector.DTO;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SenderRole
{
	[JsonStringEnumMemberName("unknown")] Unknown,
	[JsonStringEnumMemberName("interviewer")] Interviewer,
	[JsonStringEnumMemberName("interviewee")] Interviewee
}