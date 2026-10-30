using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using aissyst.core.LLMConnector.DTO;

namespace chatgpt_connector;

internal sealed class ResponsesApiException(string code, int? status = null, string? serverMessage = null,
	string? errorType = null, string? parameter = null, string? requestId = null)
	: IOException(FormatMessage(code, status, serverMessage, errorType, parameter, requestId))
{
	internal string Code { get; } = code;
	internal int? Status { get; } = status;
	internal string? ServerMessage { get; } = serverMessage;
	internal string? ErrorType { get; } = errorType;
	internal string? Parameter { get; } = parameter;
	internal string? RequestId { get; } = requestId;
	internal bool IsAuthorizationError => Status is 401 or 403;

	private static string FormatMessage(string code, int? status, string? serverMessage,
		string? errorType, string? parameter, string? requestId)
	{
		var message = new StringBuilder($"Responses API error: {code}");
		if (status.HasValue)
		{
			message.Append($" (HTTP {status})");
		}
		message.Append('.');
		foreach (var (name, value) in new[] { ("Message", serverMessage), ("Type", errorType),
			("Parameter", parameter), ("Request ID", requestId) })
		{
			if (!string.IsNullOrWhiteSpace(value))
			{
				message.Append($" {name}: {value}");
			}
		}
		return message.ToString();
	}
}

internal static class ResponsesProtocol
{
	private static readonly JsonSerializerOptions SerializerOptions = new()
	{
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
	};

	private static readonly JsonElement Schema = JsonDocument.Parse("""
		{"type":"object","properties":{
		 "request_id":{"type":"integer"},"error":{"type":"string"},
		 "summary":{"type":"string"},"summary_ts":{"type":["string","null"]},
		 "message":{"type":"string"}},
		 "required":["request_id","error","summary","summary_ts","message"],"additionalProperties":false}
		""").RootElement.Clone();

	internal static string Serialize<T>(T value) => JsonSerializer.Serialize(value, SerializerOptions);

	internal static bool IsSupportedReasoningEffort(string effort) =>
		effort is "none" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max";

	internal static string Create(string model, string role, string text, string? previousId, bool generate, string? reasoningEffort = null)
	{
		var body = new Dictionary<string, object?>
		{
			["type"] = "response.create", ["model"] = model, ["store"] = false,
			["input"] = new[] { new { type = "message", role, content = new[] { new { type = "input_text", text } } } }
		};
		if (reasoningEffort != null)
		{
			if (!IsSupportedReasoningEffort(reasoningEffort))
			{
				throw new ArgumentException("The reasoning effort is not supported by the Responses API.", nameof(reasoningEffort));
			}
			body["reasoning"] = new { effort = reasoningEffort };
		}
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
		return Serialize(body);
	}

	internal static ResponsesApiException ParseError(JsonElement item)
	{
		var response = Property(item, "response");
		var error = Property(item, "error");
		if (error.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
		{
			error = Property(response, "error");
		}
		var status = Property(item, "status");
		var message = ReadString(Property(error, "message"))
			?? DiagnosticText(Property(error, "detail"))
			?? DiagnosticText(Property(item, "detail"))
			?? ReadString(Property(item, "message"))
			?? ReadString(error);
		var reason = ReadString(Property(Property(response, "incomplete_details"), "reason"));
		if (reason != null)
		{
			message = $"{message ?? "The response is incomplete."} Reason: {reason}";
		}
		return new ResponsesApiException(ReadString(Property(error, "code")) ?? "unspecified_error",
			status.ValueKind == JsonValueKind.Number && status.TryGetInt32(out var value) ? value : null,
			message, ReadString(Property(error, "type")), ReadString(Property(error, "param")),
			ReadString(Property(item, "request_id")) ?? ReadString(Property(error, "request_id")));
	}

	private static JsonElement Property(JsonElement item, string name) =>
		item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) ? value : default;

	private static string? ReadString(JsonElement item) =>
		item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()) ? item.GetString() : null;

	private static string? DiagnosticText(JsonElement item) => item.ValueKind switch
	{
		JsonValueKind.Object or JsonValueKind.Array => item.GetRawText(),
		_ => ReadString(item)
	};

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
		var payload = text.ToString();
		if (string.IsNullOrWhiteSpace(payload))
		{
			throw new InvalidDataException("The Responses API response contains no output_text. " +
				$"Response ID: {ReadString(Property(response, "id")) ?? "unknown"}; " +
				$"status: {ReadString(Property(response, "status")) ?? "unknown"}.");
		}
		using var document = JsonDocument.Parse(payload);
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
