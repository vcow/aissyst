using System.Text;
using System.Text.Json;
using aissyst.core.LLMConnector.DTO;

namespace chatgpt_connector;

internal sealed class ResponsesApiException(string code, int? status = null)
	: IOException($"Responses API error: {code}" + (status.HasValue ? $" (HTTP {status})." : "."))
{
	internal string Code { get; } = code;
	internal bool IsAuthorizationError => status is 401 or 403;
}

internal static class ResponsesProtocol
{
	private static readonly JsonElement Schema = JsonDocument.Parse("""
		{"type":"object","properties":{
		 "request_id":{"type":"integer"},"error":{"type":"string"},
		 "summary":{"type":"string"},"summary_ts":{"type":["string","null"]},
		 "message":{"type":"string"}},
		 "required":["request_id","error","summary","summary_ts","message"],"additionalProperties":false}
		""").RootElement.Clone();

	internal static string Create(string model, string role, string text, string? previousId, bool generate, string streamId = "interview")
	{
		var body = new Dictionary<string, object?>
		{
			["type"] = "response.create", ["stream_id"] = streamId, ["model"] = model, ["store"] = false,
			["input"] = new[] { new { type = "message", role, content = new[] { new { type = "input_text", text } } } }
		};
		if (previousId != null)
		{
			body["previous_response_id"] = previousId;
		}
		if (generate)
		{
			body["text"] = new { format = new { type = "json_schema", name = "dialog_response", strict = true, schema = Schema } };
		}
		else
		{
			body["generate"] = false;
		}
		return JsonSerializer.Serialize(body);
	}

	internal static DialogResponse Parse(JsonElement response)
	{
		var text = new StringBuilder();
		foreach (var item in response.GetProperty("output").EnumerateArray())
		{
			if (item.GetProperty("type").GetString() != "message")
			{
				continue;
			}
			foreach (var content in item.GetProperty("content").EnumerateArray())
			{
				var type = content.GetProperty("type").GetString();
				if (type == "refusal")
				{
					throw new InvalidDataException("The model refused the request.");
				}
				if (type == "output_text")
				{
					text.Append(content.GetProperty("text").GetString());
				}
			}
		}
		using var document = JsonDocument.Parse(text.ToString());
		var root = document.RootElement;
		if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 5
			|| !root.TryGetProperty("request_id", out var id) || !id.TryGetInt32(out _))
		{
			throw new InvalidDataException("The model response has an invalid structure or request_id.");
		}
		foreach (var name in new[] { "error", "summary", "message" })
		{
			if (!root.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.String)
			{
				throw new InvalidDataException($"The model response requires a string {name} field.");
			}
		}
		if (!root.TryGetProperty("summary_ts", out var timestamp) || timestamp.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
		{
			throw new InvalidDataException("The model response requires a nullable summary_ts field.");
		}
		var result = root.Deserialize<DialogResponse>()!;

		return result;
	}
}
