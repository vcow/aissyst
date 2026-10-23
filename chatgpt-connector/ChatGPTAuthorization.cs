using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace chatgpt_connector;

public sealed record ChatGPTAuthorizationCode(string Code, string ClientId, string RedirectUri);

public interface IChatGPTAuthorization
{
	Task<ChatGPTAuthorizationCode> AuthorizeAsync(IReadOnlyDictionary<string, string> parameters,
		CancellationToken cancellationToken);
}

public sealed class LoopbackChatGPTAuthorization : IChatGPTAuthorization
{
	private readonly ILogger<LoopbackChatGPTAuthorization> _logger;
	private readonly Action<Uri> _openBrowser;

	public LoopbackChatGPTAuthorization(ILogger<LoopbackChatGPTAuthorization> logger)
		: this(logger, uri =>
		{
			using var browser = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
		})
	{
	}

	internal LoopbackChatGPTAuthorization(ILogger<LoopbackChatGPTAuthorization> logger, Action<Uri> openBrowser)
	{
		_logger = logger;
		_openBrowser = openBrowser;
	}

	public async Task<ChatGPTAuthorizationCode> AuthorizeAsync(IReadOnlyDictionary<string, string> parameters,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		using var listener = StartListener(out var redirectUri);
		var query = parameters.Append(new KeyValuePair<string, string>("redirect_uri", redirectUri));
		var authorizationUri = "https://auth.openai.com/api/accounts/authorize?" + string.Join("&",
			query.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
		_logger.LogInformation("Continue with ChatGPT: complete sign-in in your browser.");
		// Authorization URLs may contain an ID token hint. Never write them to logs.
		_openBrowser(new Uri(authorizationUri));
		using var registration = cancellationToken.Register(listener.Stop);
		while (true)
		{
			HttpListenerContext context;
			try
			{
				context = await listener.GetContextAsync().WaitAsync(cancellationToken);
			}
			catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
			{
				throw new OperationCanceledException(cancellationToken);
			}
			catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
			{
				throw new OperationCanceledException(cancellationToken);
			}
			var request = context.Request;
			var values = request.QueryString;
			if (request.HttpMethod != "GET" || request.Url?.AbsolutePath != "/auth/callback"
				|| values["state"] != parameters["state"])
			{
				await RespondAsync(context.Response, 400, "Invalid sign-in callback.", cancellationToken);
				continue;
			}
			var clientId = values["client_id"];
			var previousClientId = parameters["client_id"];
			if (previousClientId != "dynamic_agent_client")
			{
				if (clientId != null && clientId != previousClientId)
				{
					await RespondAsync(context.Response, 400, "The ChatGPT registration does not match.", cancellationToken);
					throw new InvalidOperationException("The ChatGPT registration does not match the selected account.");
				}
				clientId = previousClientId;
			}
			if (values["error"] != null || string.IsNullOrWhiteSpace(values["code"])
				|| string.IsNullOrWhiteSpace(clientId) || clientId == "dynamic_agent_client")
			{
				await RespondAsync(context.Response, 400, "ChatGPT sign-in was not completed. Return to aissyst.", cancellationToken);
				throw new InvalidOperationException("ChatGPT sign-in was denied or returned an incomplete registration.");
			}
			await RespondAsync(context.Response, 200, "Authorization received. You can return to aissyst while the account is verified.", cancellationToken);
			return new ChatGPTAuthorizationCode(values["code"]!, clientId!, redirectUri);
		}
	}

	private static HttpListener StartListener(out string redirectUri)
	{
		for (var attempt = 0; attempt < 5; attempt++)
		{
			int port;
			using (var socket = new TcpListener(IPAddress.Loopback, 0))
			{
				socket.Start();
				port = ((IPEndPoint)socket.LocalEndpoint).Port;
			}
			var listener = new HttpListener();
			listener.Prefixes.Add($"http://127.0.0.1:{port}/");
			try
			{
				listener.Start();
				redirectUri = $"http://127.0.0.1:{port}/auth/callback";
				return listener;
			}
			catch (HttpListenerException)
			{
				listener.Close();
			}
		}
		throw new IOException("Could not start the ChatGPT loopback callback listener.");
	}

	private static async Task RespondAsync(HttpListenerResponse response, int status, string message,
		CancellationToken cancellationToken)
	{
		using (response)
		{
			response.StatusCode = status;
			response.ContentType = "text/plain; charset=utf-8";
			response.Headers["Cache-Control"] = "no-store";
			var bytes = Encoding.UTF8.GetBytes(message);
			response.ContentLength64 = bytes.Length;
			await response.OutputStream.WriteAsync(bytes, cancellationToken);
		}
	}
}
