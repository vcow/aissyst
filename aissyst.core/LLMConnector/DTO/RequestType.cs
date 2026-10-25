using System.Text.Json.Serialization;

namespace aissyst.core.LLMConnector.DTO;

[Flags, JsonConverter(typeof(JsonStringEnumConverter))]
public enum RequestType
{
	[JsonStringEnumMemberName("summary")] Summary = 0x00,
	[JsonStringEnumMemberName("translate")] Translate = 0x01,
	[JsonStringEnumMemberName("advice")] Advice = 0x02,
	[JsonStringEnumMemberName("reply")] Reply = 0x04,
	[JsonStringEnumMemberName("t_advice")] TranslateAndAdvice = Translate | Advice,
	[JsonStringEnumMemberName("t_reply")] TranslateAndReply = Translate | Reply
}