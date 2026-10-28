using System.Net;
using System.Net.WebSockets;
using System.Text;

namespace chatgpt_connector;

internal interface IResponsesSocket : IDisposable
{
	Task ConnectAsync(string accessToken, CancellationToken cancellationToken);
	Task SendAsync(string message, CancellationToken cancellationToken);
	Task<string> ReceiveAsync(CancellationToken cancellationToken);
}

internal sealed class ResponsesSocket : IResponsesSocket
{
	private readonly ClientWebSocket _socket = new();

	public async Task ConnectAsync(string accessToken, CancellationToken cancellationToken)
	{
		_socket.Options.CollectHttpResponseDetails = true;
		_socket.Options.SetRequestHeader("Authorization", "Bearer " + accessToken);
		_socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
		_socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(20);
		try
		{
			await _socket.ConnectAsync(new Uri("wss://api.openai.com/v1/responses"), cancellationToken);
		}
		catch (WebSocketException) when (_socket.HttpStatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
		{
			throw new ResponsesApiException("authorization_failed", (int)_socket.HttpStatusCode);
		}
	}

	public Task SendAsync(string message, CancellationToken cancellationToken) =>
		_socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(message)), WebSocketMessageType.Text, true, cancellationToken);

	public async Task<string> ReceiveAsync(CancellationToken cancellationToken)
	{
		using var message = new MemoryStream();
		var buffer = new byte[8192];
		WebSocketReceiveResult part;
		do
		{
			part = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
			if (part.MessageType == WebSocketMessageType.Close)
			{
				throw new IOException("The Responses WebSocket connection was closed.");
			}
			if (part.MessageType != WebSocketMessageType.Text)
			{
				throw new InvalidDataException("Expected a text WebSocket message.");
			}
			message.Write(buffer, 0, part.Count);
		} while (!part.EndOfMessage);
		return new UTF8Encoding(false, true).GetString(message.GetBuffer(), 0, checked((int)message.Length));
	}

	public void Dispose() => _socket.Dispose();
}
