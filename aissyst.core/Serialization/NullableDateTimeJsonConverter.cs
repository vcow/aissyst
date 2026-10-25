using System.Text.Json;
using System.Text.Json.Serialization;

namespace aissyst.core.Serialization;

public sealed class NullableDateTimeJsonConverter : JsonConverter<DateTime?>
{
	public override bool HandleNull => true;

	public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
	{
		if (reader.TokenType == JsonTokenType.Null)
		{
			return null;
		}
		if (reader.TokenType == JsonTokenType.String)
		{
			if (string.IsNullOrWhiteSpace(reader.GetString()))
			{
				return null;
			}
			if (reader.TryGetDateTime(out var timestamp))
			{
				return timestamp;
			}
		}
		throw new JsonException("Expected an ISO 8601 date-time string, an empty string, or null.");
	}

	public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
	{
		if (value.HasValue)
		{
			writer.WriteStringValue(value.Value);
		}
		else
		{
			writer.WriteNullValue();
		}
	}
}
