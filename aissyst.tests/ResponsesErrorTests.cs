using System.Collections.Concurrent;
using System.Text.Json;
using aissyst.core.LLMConnector.DTO;
using chatgpt_connector;
using Microsoft.Extensions.Logging;

namespace aissyst.tests;

public sealed class ResponsesErrorTests
{
	[Theory]
	[InlineData("""{"type":"error","status":400,"error":{"code":null,"message":"Unsupported parameter: generate"}}""", "Unsupported parameter: generate")]
	[InlineData("""{"type":"error","status":400,"detail":"Request was rejected"}""", "Request was rejected")]
	[InlineData("""{"type":"error","status":400,"error":"Invalid request body"}""", "Invalid request body")]
	[InlineData("""{"type":"error","status":400,"error":null,"detail":[{"msg":"Field is required","loc":["body","input"]}]}""", "Field is required")]
	public void ErrorWithoutCodeRetainsDiagnosticMessage(string json, string expected)
	{
		using var document = JsonDocument.Parse(json);
		var exception = ResponsesProtocol.ParseError(document.RootElement);
		Assert.Equal("unspecified_error", exception.Code);
		Assert.Equal(400, exception.Status);
		Assert.Contains(expected, exception.ServerMessage);
		Assert.Contains(expected, exception.Message);
		Assert.Contains("HTTP 400", exception.Message);
	}

	[Fact]
	public async Task WarmupFailureWithoutCodeIsLoggedWithServerDetailsAndRequestId()
	{
		using var socket = new ResponsesConnectionTests.FakeSocket
		{
			ErrorEvent = _ => new
			{
				type = "error", status = 400, request_id = "req_warmup",
				error = new { type = "invalid_request_error", message = "Unsupported parameter: generate", param = "generate" }
			}
		};
		var logger = new TestLogger();
		await using var connection = Create(socket, logger);
		var exception = await Assert.ThrowsAsync<ResponsesApiException>(() =>
			connection.InitializeSessionAsync("Rules", new Session(), default));
		Assert.Equal("invalid_request_error", exception.ErrorType);
		Assert.Equal("generate", exception.Parameter);
		Assert.Equal("req_warmup", exception.RequestId);
		Assert.Contains("Unsupported parameter: generate", exception.Message);
		Assert.Contains("req_warmup", exception.Message);
		var log = Assert.Single(logger.Errors);
		Assert.Contains("Unsupported parameter: generate", log);
		Assert.Contains("req_warmup", log);
		Assert.Contains("invalid_request_error", log);
		Assert.Contains("HTTP status: 400", log);
		Assert.Contains("Model: test-model", log);
		Assert.Single(socket.Sent);
	}

	[Theory]
	[InlineData("response.failed", "Inference was stopped.")]
	[InlineData("response.incomplete", "max_output_tokens")]
	public async Task TerminalFailureIsLoggedWithoutInvalidatingParent(string eventType, string expected)
	{
		using var socket = new ResponsesConnectionTests.FakeSocket();
		var logger = new TestLogger();
		await using var connection = Create(socket, logger);
		var initial = await connection.InitializeSessionAsync("Rules", new Session(), default);
		using var parent = initial.Continuation;
		socket.ErrorEvent = item => new
		{
			type = eventType,
			response = new
			{
				id = "resp_failure",
				error = eventType == "response.failed" ? new { code = "server_error", message = "Inference was stopped." } : null,
				incomplete_details = eventType == "response.incomplete" ? new { reason = "max_output_tokens" } : null
			}
		};
		var exception = await Assert.ThrowsAsync<ResponsesApiException>(() =>
			connection.SendAsync(new DialogRequest { RequestId = 1 }, parent, default));
		Assert.Contains(expected, exception.Message);
		var log = Assert.Single(logger.Errors);
		Assert.Contains(expected, log);
		Assert.Contains(eventType, log);
		Assert.Contains("resp_failure", log);
		Assert.True(parent.IsValid);
		socket.ErrorEvent = null;
		var next = await connection.SendAsync(new DialogRequest { RequestId = 2 }, parent, default);
		next.Continuation.Dispose();
		Assert.Equal(socket.Id(2), socket.Sent.Last().GetProperty("previous_response_id").GetString());
	}

	private static ResponsesConnection Create(ResponsesConnectionTests.FakeSocket socket, ILogger logger) =>
		new("test-model", _ => Task.FromResult("test-access-token"), new ChatGPTConnectorOptions(), logger, () => socket);

	private sealed class TestLogger : ILogger
	{
		internal ConcurrentQueue<string> Errors { get; } = new();
		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
		public bool IsEnabled(LogLevel logLevel) => true;
		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
			Func<TState, Exception?, string> formatter)
		{
			if (logLevel == LogLevel.Error)
			{
				Errors.Enqueue(formatter(state, exception));
			}
		}
	}
}
