using System.Text.Json;
using aissyst.core.LLMConnector.DTO;

namespace aissyst.tests;

public sealed class DialogResponseSerializationTests
{
	[Theory]
	[InlineData("{}")]
	[InlineData("{\"summary_ts\":null}")]
	[InlineData("{\"summary_ts\":\"\"}")]
	[InlineData("{\"summary_ts\":\" \\t\\r\\n \"}")]
	public void MissingOrEmptySummaryTimestampBecomesNull(string json)
	{
		var response = JsonSerializer.Deserialize<DialogResponse>(json);
		Assert.NotNull(response);
		Assert.Null(response.SummaryTimestamp);
	}

	[Fact]
	public void UtcTimestampPreservesPrecisionAndRoundTrips()
	{
		const string timestamp = "2026-10-25T12:34:56.1234567Z";
		var response = JsonSerializer.Deserialize<DialogResponse>($"{{\"summary_ts\":\"{timestamp}\"}}");
		Assert.NotNull(response?.SummaryTimestamp);
		Assert.Equal(DateTimeKind.Utc, response.SummaryTimestamp.Value.Kind);
		Assert.Equal(timestamp, response.SummaryTimestamp.Value.ToString("O"));
		var restored = JsonSerializer.Deserialize<DialogResponse>(JsonSerializer.Serialize(response));
		Assert.Equal(response.SummaryTimestamp, restored?.SummaryTimestamp);
	}

	[Fact]
	public void NullTimestampIsOmittedWhenWritingResponse()
	{
		using var json = JsonDocument.Parse(JsonSerializer.Serialize(new DialogResponse()));
		Assert.False(json.RootElement.TryGetProperty("summary_ts", out _));
	}

	[Theory]
	[InlineData("{\"summary_ts\":\"invalid\"}")]
	[InlineData("{\"summary_ts\":\"2026-99-99T12:00:00Z\"}")]
	[InlineData("{\"summary_ts\":0}")]
	[InlineData("{\"summary_ts\":false}")]
	[InlineData("{\"summary_ts\":{}}")]
	public void InvalidTimestampIsRejectedInsteadOfDiscardingTheProcessingBoundary(string json)
	{
		Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<DialogResponse>(json));
	}
}
