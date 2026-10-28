using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using aissyst.core.LLMConnector;
using aissyst.core.LLMConnector.DTO;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace chatgpt_connector;

public sealed class ChatGPTConnector : ILLMConnector, ILLMSessionLifecycle, IAsyncDisposable
{
	private const string Issuer = "https://auth.openai.com";
	private const string Resource = "https://api.openai.com/v1";
	private readonly HttpClient _httpClient;
	private readonly IChatGPTAuthorization _authorization;
	private readonly ChatGPTConnectorOptions _options;
	private readonly ILogger<ChatGPTConnector> _logger;
	private readonly IConfigurationManager<OpenIdConnectConfiguration> _configuration;
	private readonly ChatGPTCredentialStore _store;
	private readonly CancellationTokenSource _shutdown = new();
	private readonly Lock _lifecycleLock = new();
	private Task? _initialization;
	private Task? _disposal;
	private FileStream? _sessionLock;
	private ChatGPTAccount? _account;

	private readonly IModelSelector? _modelSelector;
	private ChatGPTCredentials? _credentials;
	private ResponsesConnection? _responses;

	public ChatGPTAccount? Account => Volatile.Read(ref _account);

	public void ConfigureSession(string instructions, Func<CancellationToken, Task<Session>> getSession,
		Action<Session, DialogResponse> acceptRecovery, Func<DialogResponse, bool> acceptResponse) =>
		(_responses ?? throw new InvalidOperationException("The connector has not been initialized."))
			.Configure(instructions, getSession, acceptRecovery, acceptResponse);

	public Task<DialogResponse> InitializeSession(Session session, CancellationToken cancellationToken = default) =>
		(_responses ?? throw new InvalidOperationException("The connector has not been initialized."))
			.InitializeSessionAsync(session, cancellationToken);

	public Task<DialogResponse> SendRequest(DialogRequest request, CancellationToken cancellationToken = default) =>
		(_responses ?? throw new InvalidOperationException("The connector has not been initialized."))
			.SendAsync(request, cancellationToken);

	public ChatGPTConnector(HttpClient httpClient, IChatGPTAuthorization authorization,
		ChatGPTConnectorOptions options, ILogger<ChatGPTConnector> logger, IModelSelector modelSelector)
		: this(httpClient, authorization, options, logger,
			new ConfigurationManager<OpenIdConnectConfiguration>(Issuer + "/.well-known/openid-configuration",
				new OpenIdConnectConfigurationRetriever(), new HttpDocumentRetriever(httpClient)), modelSelector)
	{
	}

	internal ChatGPTConnector(HttpClient httpClient, IChatGPTAuthorization authorization,
		ChatGPTConnectorOptions options, ILogger<ChatGPTConnector> logger,
		IConfigurationManager<OpenIdConnectConfiguration> configuration, IModelSelector? modelSelector = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(options.ApplicationName);
		ArgumentException.ThrowIfNullOrWhiteSpace(options.StorageDirectory);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.LoginTimeout, TimeSpan.Zero);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.ConnectionTimeout, TimeSpan.Zero);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.ResponseTimeout, TimeSpan.Zero);
		ArgumentOutOfRangeException.ThrowIfLessThan(options.ReconnectDelay, TimeSpan.Zero);
		ArgumentOutOfRangeException.ThrowIfLessThan(options.ReconnectAttempts, 1);
		_modelSelector = modelSelector;
		_httpClient = httpClient;
		_authorization = authorization;
		_options = options;
		_logger = logger;
		_configuration = configuration;
		_store = new ChatGPTCredentialStore(options.StorageDirectory);
	}

	public Task InitializeAsync(CancellationToken cancellationToken = default)
	{
		lock (_lifecycleLock)
		{
			ObjectDisposedException.ThrowIf(_disposal != null, this);
			cancellationToken.ThrowIfCancellationRequested();
			return (_initialization ??= InitializeObservedAsync(cancellationToken)).WaitAsync(cancellationToken);
		}
	}

	private async Task InitializeObservedAsync(CancellationToken cancellationToken)
	{
		try
		{
			await InitializeCoreAsync(cancellationToken);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _shutdown.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception)
		{
			_logger.LogError(exception, "ChatGPT initialization failed.");
			throw;
		}
	}

	private async Task InitializeCoreAsync(CancellationToken cancellationToken)
	{
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
		cancellationToken = linked.Token;
		_sessionLock = _store.AcquireLock();
		var credentials = await _store.LoadAsync(cancellationToken);
		// Preserve the host ID even if first-time sign-in is interrupted.
		await _store.SaveAsync(credentials, cancellationToken);
		var profile = credentials.Profile;
		if (profile != null)
		{
			await ValidateIdentityAsync(profile.IdToken, profile.ClientId, null, profile.Subject, false, cancellationToken);
			if (profile.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(1))
			{
				profile = await TryRefreshAsync(profile, cancellationToken);
			}
		}
		if (profile == null)
		{
			profile = await SignInAsync(credentials, cancellationToken);
		}
		_credentials = credentials;
		credentials.Profile = profile;
		credentials.PendingClientId = null;
		await _store.SaveAsync(credentials, cancellationToken);
		var canUsePlan = profile.Scopes.Contains("chatgpt.tokens.use.direct", StringComparer.Ordinal)
			&& profile.Scopes.Contains("resource.invoke", StringComparer.Ordinal);
		Volatile.Write(ref _account, new ChatGPTAccount(profile.Subject, profile.Email, canUsePlan));
		_logger.LogInformation("Connected to ChatGPT as {Account}.", profile.Email ?? profile.Subject);
		if (canUsePlan)
		{
			_logger.LogInformation("ChatGPT plan usage is enabled. Future AI requests will use your ChatGPT plan or credits. Manage access and usage in ChatGPT Settings.");
		}
		else
		{
			_logger.LogWarning("Signed in, but ChatGPT plan usage was not granted. AI requests are unavailable.");
		}
		if (_modelSelector != null)
		{
			if (!canUsePlan)
			{
				throw new UnauthorizedAccessException("ChatGPT plan usage permission is required for inference.");
			}
			var models = await GetModelsAsync(profile.AccessToken, cancellationToken);
			await _modelSelector.InitializeAsync(models, cancellationToken);
			var selected = _modelSelector.GetSelectedModel();
			if (!models.Any(model => model.Id == selected.Id))
			{
				throw new InvalidOperationException("The selected model is not available for this account.");
			}
			_responses = new ResponsesConnection(selected.Id, GetAccessTokenAsync, _options, _logger);
		}
	}

	private async Task<IReadOnlyList<LLMModel>> GetModelsAsync(string accessToken, CancellationToken cancellationToken)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, Resource + "/models");
		request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
		using var response = await _httpClient.SendAsync(request, cancellationToken);
		if (!response.IsSuccessStatusCode)
		{
			throw new HttpRequestException($"Failed to load ChatGPT models (HTTP {(int)response.StatusCode}).", null, response.StatusCode);
		}
		using var document = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken)
			?? throw new InvalidDataException("The model catalog is empty.");
		return document.RootElement.GetProperty("models").EnumerateArray()
			.Where(model => OptionalString(model, "visibility") == "list")
			.Select(model => new LLMModel(OptionalString(model, "slug")
				?? throw new InvalidDataException("A model slug is missing."),
				OptionalString(model, "display_name") ?? OptionalString(model, "slug")!)).ToArray();
	}

	private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
	{
		var credentials = _credentials ?? throw new InvalidOperationException("ChatGPT credentials are not initialized.");
		var profile = credentials.Profile!;
		if (profile.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(1))
		{
			profile = await TryRefreshAsync(profile, cancellationToken)
				?? throw new UnauthorizedAccessException("The ChatGPT session has expired. Restart the application to sign in again.");
			if (!profile.Scopes.Contains("chatgpt.tokens.use.direct", StringComparer.Ordinal)
				|| !profile.Scopes.Contains("resource.invoke", StringComparer.Ordinal))
			{
				throw new UnauthorizedAccessException("ChatGPT plan usage permission was revoked.");
			}
			credentials.Profile = profile;
			await _store.SaveAsync(credentials, cancellationToken);
		}
		return profile.AccessToken;
	}

	private async Task<ChatGPTProfile> SignInAsync(ChatGPTCredentials credentials, CancellationToken cancellationToken)
	{
		var previous = credentials.Profile;
		var clientId = previous?.ClientId ?? credentials.PendingClientId ?? "dynamic_agent_client";
		var verifier = RandomValue();
		var nonce = RandomValue();
		var parameters = new Dictionary<string, string>
		{
			["client_id"] = clientId, ["ext_agent_host_id"] = credentials.HostId,
			["response_type"] = "code",
			["scope"] = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct",
			["resource"] = Resource, ["state"] = RandomValue(), ["nonce"] = nonce,
			["code_challenge_method"] = "S256",
			["code_challenge"] = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
		};
		if (clientId == "dynamic_agent_client")
		{
			parameters["agent_name_hint"] = _options.ApplicationName;
		}
		if (previous != null)
		{
			parameters["id_token_hint"] = previous.IdToken;
		}
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(_options.LoginTimeout);
		ChatGPTAuthorizationCode code;
		try
		{
			code = await _authorization.AuthorizeAsync(parameters, timeout.Token);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			throw new TimeoutException("ChatGPT sign-in timed out. Restart the application to try again.");
		}
		if (string.IsNullOrWhiteSpace(code.ClientId) || code.ClientId == "dynamic_agent_client"
			|| (clientId != "dynamic_agent_client" && clientId != code.ClientId))
		{
			throw new InvalidOperationException("ChatGPT returned an invalid client registration.");
		}
		credentials.PendingClientId = code.ClientId;
		await _store.SaveAsync(credentials, cancellationToken);
		using var response = await ExchangeAsync(new Dictionary<string, string>
		{
			["grant_type"] = "authorization_code", ["client_id"] = code.ClientId,
			["code"] = code.Code, ["code_verifier"] = verifier,
			["redirect_uri"] = code.RedirectUri, ["resource"] = Resource
		}, cancellationToken);
		return await ReadProfileAsync(response.RootElement, code.ClientId, nonce, previous, cancellationToken);
	}

	private async Task<ChatGPTProfile?> TryRefreshAsync(ChatGPTProfile previous, CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(previous.RefreshToken))
		{
			return null;
		}
		_logger.LogInformation("Renewing the ChatGPT session.");
		try
		{
			using var response = await ExchangeAsync(new Dictionary<string, string>
			{
				["grant_type"] = "refresh_token", ["client_id"] = previous.ClientId,
				["refresh_token"] = previous.RefreshToken, ["resource"] = Resource
			}, cancellationToken);
			return await ReadProfileAsync(response.RootElement, previous.ClientId, null, previous, cancellationToken);
		}
		catch (ChatGPTOAuthException exception) when (exception.InvalidGrant)
		{
			_logger.LogWarning("The ChatGPT session has expired or was revoked. Sign in again in your browser.");
			return null;
		}
	}

	private async Task<JsonDocument> ExchangeAsync(Dictionary<string, string> fields, CancellationToken cancellationToken)
	{
		using var content = new FormUrlEncodedContent(fields);
		using var response = await _httpClient.PostAsync(Issuer + "/api/accounts/oauth/token", content, cancellationToken);
		if (!response.IsSuccessStatusCode)
		{
			var invalidGrant = false;
			try
			{
				using var error = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken);
				invalidGrant = error?.RootElement.TryGetProperty("error", out var value) == true
					&& value.ValueKind == JsonValueKind.String && value.GetString() == "invalid_grant";
			}
			catch (JsonException)
			{
			}
			// Never include response bodies: they can contain credentials.
			throw new ChatGPTOAuthException((int)response.StatusCode, invalidGrant);
		}
		return await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken)
			?? throw new InvalidDataException("ChatGPT returned an empty token response.");
	}

	private async Task<ChatGPTProfile> ReadProfileAsync(JsonElement response, string clientId, string? nonce,
		ChatGPTProfile? previous, CancellationToken cancellationToken)
	{
		var idToken = OptionalString(response, "id_token") ?? (nonce == null ? previous?.IdToken : null)
			?? throw new InvalidDataException("ChatGPT did not return an ID token.");
		var identity = await ValidateIdentityAsync(idToken, clientId, nonce, previous?.Subject,
			nonce != null || idToken != previous?.IdToken, cancellationToken);
		if (!response.TryGetProperty("expires_in", out var expiry) || !expiry.TryGetInt32(out var seconds) || seconds <= 0)
		{
			throw new InvalidDataException("ChatGPT returned an invalid access token lifetime.");
		}
		if (!string.Equals(OptionalString(response, "token_type"), "Bearer", StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException("ChatGPT returned an unsupported token type.");
		}
		return new ChatGPTProfile
		{
			ClientId = clientId, Subject = identity.Subject, Email = identity.Email, IdToken = idToken,
			AccessToken = OptionalString(response, "access_token")
				?? throw new InvalidDataException("ChatGPT did not return an access token."),
			RefreshToken = OptionalString(response, "refresh_token") ?? previous?.RefreshToken,
			Scopes = OptionalString(response, "scope")?.Split(' ', StringSplitOptions.RemoveEmptyEntries)
				?? (nonce == null ? previous?.Scopes : null) ?? [],
			ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(seconds)
		};
	}

	private async Task<ChatGPTAccount> ValidateIdentityAsync(string idToken, string clientId, string? nonce,
		string? expectedSubject, bool validateLifetime, CancellationToken cancellationToken)
	{
		var configuration = await _configuration.GetConfigurationAsync(cancellationToken);
		if (configuration.Issuer != Issuer)
		{
			throw new InvalidDataException("The OpenAI discovery issuer is invalid.");
		}
		var result = await new JsonWebTokenHandler().ValidateTokenAsync(idToken, new TokenValidationParameters
		{
			ValidIssuer = Issuer, ValidAudience = clientId, IssuerSigningKeys = configuration.SigningKeys,
			ValidateIssuer = true, ValidateAudience = true, ValidateIssuerSigningKey = true,
			RequireSignedTokens = true, RequireExpirationTime = true, ValidateLifetime = validateLifetime,
			ValidAlgorithms = [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.EcdsaSha256],
			ClockSkew = TimeSpan.FromMinutes(1)
		});
		if (!result.IsValid)
		{
			throw new InvalidDataException("The ChatGPT ID token could not be verified.");
		}
		var subject = result.ClaimsIdentity.FindFirst("sub")?.Value;
		if (string.IsNullOrWhiteSpace(subject) || (expectedSubject != null && subject != expectedSubject)
			|| (nonce != null && result.ClaimsIdentity.FindFirst("nonce")?.Value != nonce))
		{
			throw new InvalidDataException("The ChatGPT identity or sign-in nonce does not match.");
		}
		return new ChatGPTAccount(subject, result.ClaimsIdentity.FindFirst("email")?.Value, false);
	}

	public ValueTask DisposeAsync()
	{
		lock (_lifecycleLock)
		{
			return new ValueTask(_disposal ??= DisposeCoreAsync());
		}
	}

	private async Task DisposeCoreAsync()
	{
		await _shutdown.CancelAsync();
		if (_initialization != null)
		{
			try
			{
				await _initialization;
			}
			catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
			{
			}
			catch (Exception)
			{
				// InitializeObservedAsync has already logged the failure and propagated it to the caller.
			}
		}
		if (_responses != null)
		{
			await _responses.DisposeAsync();
		}
		Volatile.Write(ref _account, null);
		_sessionLock?.Dispose();
		_shutdown.Dispose();
	}

	private static string RandomValue() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
	private static string? OptionalString(JsonElement element, string property) =>
		element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
			&& !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString() : null;

	private sealed class ChatGPTOAuthException(int statusCode, bool invalidGrant)
		: Exception($"ChatGPT token exchange failed (HTTP {statusCode}).")
	{
		public bool InvalidGrant { get; } = invalidGrant;
	}
}
