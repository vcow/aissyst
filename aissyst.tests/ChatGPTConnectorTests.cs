using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using chatgpt_connector;
using aissyst.core.LLMConnector;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace aissyst.tests;

public sealed class ChatGPTConnectorTests : IDisposable
{
	private readonly string _directory = Path.Combine(Path.GetTempPath(), $"aissyst-chatgpt-tests-{Guid.NewGuid():N}");
	private readonly RSA _rsa = RSA.Create(2048);
	private Dictionary<string, string>? _authorizationParameters;
	private int _browserRequests;
	private readonly List<Dictionary<string, string>> _tokenRequests = [];
	private string _tokenFault = "";
	private int _lifetime = 3600;
	private bool _rejectRefresh;
	private bool _grantPlan = true;

	[Fact]
	public async Task FirstSignInVerifiesIdentitySavesPrivateCredentialsAndUsesPkce()
	{
		using var client = CreateClient();
		await using var connector = Create(client);
		await Task.WhenAll(connector.InitializeAsync(), connector.InitializeAsync());
		Assert.Equal("user-123", connector.Account?.Subject);
		Assert.Equal("test@example.test", connector.Account?.Email);
		Assert.True(connector.Account?.CanUseChatGPTPlan);
		Assert.Equal(1, _browserRequests);
		Assert.Equal("dynamic_agent_client", _authorizationParameters!["client_id"]);
		Assert.Equal("aissyst", _authorizationParameters["agent_name_hint"]);
		Assert.Equal("S256", _authorizationParameters["code_challenge_method"]);
		var tokenRequest = Assert.Single(_tokenRequests);
		Assert.Equal("oaiapp_test", tokenRequest["client_id"]);
		Assert.Equal("http://127.0.0.1:12345/auth/callback", tokenRequest["redirect_uri"]);
		Assert.Equal(_authorizationParameters["code_challenge"], Base64UrlEncoder.Encode(
			SHA256.HashData(Encoding.ASCII.GetBytes(tokenRequest["code_verifier"]))));
		Assert.False(tokenRequest.ContainsKey("client_secret"));
		if (!OperatingSystem.IsWindows())
		{
			Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
				File.GetUnixFileMode(Path.Combine(_directory, "credentials.json")));
		}
	}

	[Fact]
	public async Task ReturningLaunchReusesAccountAndHostWithoutBrowserOrTokenExchange()
	{
		using var client = CreateClient();
		await using (var first = Create(client))
		{
			await first.InitializeAsync();
		}
		var host = (await new ChatGPTCredentialStore(_directory).LoadAsync(CancellationToken.None)).HostId;
		await using var second = Create(client);
		await second.InitializeAsync();
		Assert.NotNull(second.Account);
		Assert.Equal(1, _browserRequests);
		Assert.Single(_tokenRequests);
		Assert.Equal(host, (await new ChatGPTCredentialStore(_directory).LoadAsync(CancellationToken.None)).HostId);
	}

	[Theory]
	[InlineData("nonce")]
	[InlineData("audience")]
	[InlineData("issuer")]
	[InlineData("expired")]
	[InlineData("signature")]
	public async Task InvalidIdentityNeverBecomesConnectedOrPersisted(string fault)
	{
		_tokenFault = fault;
		using var client = CreateClient();
		await using var connector = Create(client);
		await Assert.ThrowsAsync<InvalidDataException>(() => connector.InitializeAsync());
		Assert.Null(connector.Account);
		var credentials = await new ChatGPTCredentialStore(_directory).LoadAsync(CancellationToken.None);
		Assert.Null(credentials.Profile);
		Assert.Equal("oaiapp_test", credentials.PendingClientId);
	}

	[Fact]
	public async Task RefreshRotatesCredentialsWithoutRegisteringAnotherClient()
	{
		_lifetime = 10;
		using var client = CreateClient();
		await using (var first = Create(client))
		{
			await first.InitializeAsync();
		}
		await using var second = Create(client);
		await second.InitializeAsync();
		Assert.Equal(1, _browserRequests);
		Assert.Equal("refresh_token", _tokenRequests[1]["grant_type"]);
		Assert.Equal("oaiapp_test", _tokenRequests[1]["client_id"]);
		Assert.False(_tokenRequests[1].ContainsKey("scope"));
		var credentials = await new ChatGPTCredentialStore(_directory).LoadAsync(CancellationToken.None);
		Assert.Equal("refresh-2", credentials.Profile?.RefreshToken);
	}

	[Fact]
	public async Task RevokedSessionSignsInAgainWithIssuedClientAndOriginalHost()
	{
		_lifetime = 10;
		using var client = CreateClient();
		await using (var first = Create(client))
		{
			await first.InitializeAsync();
		}
		var host = _authorizationParameters!["ext_agent_host_id"];
		_rejectRefresh = true;
		await using var second = Create(client);
		await second.InitializeAsync();
		Assert.Equal(2, _browserRequests);
		Assert.Equal("oaiapp_test", _authorizationParameters["client_id"]);
		Assert.Equal(host, _authorizationParameters["ext_agent_host_id"]);
		Assert.False(_authorizationParameters.ContainsKey("agent_name_hint"));
		Assert.True(_authorizationParameters.ContainsKey("id_token_hint"));
		Assert.NotNull(second.Account);
	}

	[Fact]
	public async Task MissingPlanPermissionIsReportedSeparatelyFromIdentity()
	{
		_grantPlan = false;
		using var client = CreateClient();
		await using var connector = Create(client);
		await connector.InitializeAsync();
		Assert.NotNull(connector.Account);
		Assert.False(connector.Account.CanUseChatGPTPlan);
	}

	[Fact]
	public async Task DisposalCancelsPendingSignInAndReleasesSessionLock()
	{
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var authorization = new TestAuthorization(async (_, cancellationToken) =>
		{
			entered.SetResult();
			await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
			throw new InvalidOperationException("Unexpected completion.");
		});
		using var client = CreateClient();
		var connector = Create(client, authorization);
		var initialization = connector.InitializeAsync();
		await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
		await connector.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialization);
		await connector.DisposeAsync();
		await Assert.ThrowsAsync<ObjectDisposedException>(() => connector.InitializeAsync());
		using var sessionLock = new ChatGPTCredentialStore(_directory).AcquireLock();
	}

	[Fact]
	public async Task LoopbackCallbackRejectsWrongStateAndAcceptsValidResponse()
	{
		var opened = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
		var flow = new LoopbackChatGPTAuthorization(NullLogger<LoopbackChatGPTAuthorization>.Instance,
			uri => opened.SetResult(uri));
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		var login = flow.AuthorizeAsync(new Dictionary<string, string>
		{
			["client_id"] = "dynamic_agent_client", ["state"] = "expected"
		}, timeout.Token);
		var uri = await opened.Task.WaitAsync(timeout.Token);
		var parameters = ParseForm(uri.Query.TrimStart('?'));
		var redirect = parameters["redirect_uri"];
		Assert.Equal("127.0.0.1", new Uri(redirect).Host);
		using var client = new HttpClient();
		using var invalid = await client.GetAsync(redirect + "?state=wrong&code=bad&client_id=oaiapp_test", timeout.Token);
		Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
		Assert.False(login.IsCompleted);
		using var valid = await client.GetAsync(redirect + "?state=expected&code=valid&client_id=oaiapp_test", timeout.Token);
		Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
		Assert.Equal(new ChatGPTAuthorizationCode("valid", "oaiapp_test", redirect), await login);
	}

	[Fact]
	public async Task InitializationPassesAccountCatalogToModelSelector()
	{
		using var client = CreateClient();
		var selector = new TestModelSelector();
		await using var connector = Create(client, modelSelector: selector);
		await connector.InitializeAsync();
		Assert.Equal(new[] { "model-a", "model-b" }, selector.Models.Select(model => model.Id));
		Assert.Equal("model-b", selector.GetSelectedModel().Id);
		var model = selector.GetSelectedModel();
		Assert.Equal(new[] { "low", "medium", "high" }, model.ReasoningLevels.Select(level => level.Effort));
		Assert.Equal("medium", model.DefaultReasoningEffort);
		Assert.Equal("Fast responses", model.ReasoningLevels[0].Description);
		Assert.Empty(selector.Models[0].ReasoningLevels);
	}

	[Theory]
	[InlineData("low", true)]
	[InlineData("none", false)]
	[InlineData("ultra", false)]
	public async Task SelectedReasoningEffortMustBelongToTheSelectedAccountModel(string effort, bool supported)
	{
		using var client = CreateClient();
		var selector = new TestModelSelector { Effort = effort };
		await using var connector = Create(client, modelSelector: selector);
		if (supported)
		{
			await connector.InitializeAsync();
		}
		else
		{
			var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => connector.InitializeAsync());
			Assert.Contains("reasoning effort", exception.Message);
		}
	}

	private sealed class TestModelSelector : IModelSelector
	{
		internal IReadOnlyList<LLMModel> Models = [];
		internal string? Effort;
		public Task InitializeAsync(IReadOnlyList<LLMModel> models, CancellationToken cancellationToken = default)
		{
			Models = models;
			return Task.CompletedTask;
		}
		public LLMModel GetSelectedModel() => Models.Last();
		public string? GetSelectedReasoningEffort() => Effort;
	}

	private ChatGPTConnector Create(HttpClient client, IChatGPTAuthorization? authorization = null, IModelSelector? modelSelector = null)
	{
		var configuration = new OpenIdConnectConfiguration { Issuer = "https://auth.openai.com" };
		configuration.SigningKeys.Add(new RsaSecurityKey(_rsa) { KeyId = "test-key" });
		return new ChatGPTConnector(client, authorization ?? new TestAuthorization((parameters, _) =>
		{
			_browserRequests++;
			_authorizationParameters = new Dictionary<string, string>(parameters);
			return Task.FromResult(new ChatGPTAuthorizationCode("test-code", "oaiapp_test", "http://127.0.0.1:12345/auth/callback"));
		}), new ChatGPTConnectorOptions { StorageDirectory = _directory }, NullLogger<ChatGPTConnector>.Instance,
			new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration), modelSelector);
	}

	private HttpClient CreateClient() => new(new TestHandler(async request =>
	{
		if (request.RequestUri?.AbsoluteUri == "https://api.openai.com/v1/models")
		{
			Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
			Assert.Equal("access-token", request.Headers.Authorization?.Parameter);
			return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""
				{"models":[
				 {"slug":"model-a","display_name":"Model A","visibility":"list"},
				 {"slug":"hidden","display_name":"Hidden","visibility":"hidden"},
				 {"slug":"model-b","display_name":"Model B","visibility":"list","default_reasoning_level":"medium",
				  "supported_reasoning_levels":[
				   {"effort":"low","description":"Fast responses"},
				   {"effort":"medium","description":"Balanced responses"},
				   {"effort":"high","description":"Deeper reasoning"},
				   {"effort":"ultra","description":"Automatic agent delegation"},
				   {"effort":"low","description":"Duplicate"},
				   {"description":"Missing effort"},null]}
				]}
				""", Encoding.UTF8, "application/json") };
		}
		Assert.Equal("https://auth.openai.com/api/accounts/oauth/token", request.RequestUri?.AbsoluteUri);
		var fields = ParseForm(await request.Content!.ReadAsStringAsync());
		_tokenRequests.Add(fields);
		if (_rejectRefresh && fields["grant_type"] == "refresh_token")
		{
			return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { error = "invalid_grant" }) };
		}
		var now = DateTime.UtcNow;
		var claims = new Dictionary<string, object>
		{
			["sub"] = "user-123", ["email"] = "test@example.test",
			["nonce"] = _tokenFault == "nonce" ? "wrong" : _authorizationParameters!["nonce"]
		};
		using var otherKey = RSA.Create(2048);
		var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
		{
			Issuer = _tokenFault == "issuer" ? "https://wrong.test" : "https://auth.openai.com",
			Audience = _tokenFault == "audience" ? "wrong-client" : "oaiapp_test",
			Claims = claims, IssuedAt = now.AddHours(-2), NotBefore = now.AddHours(-2),
			Expires = _tokenFault == "expired" ? now.AddHours(-1) : now.AddHours(1),
			SigningCredentials = new SigningCredentials(new RsaSecurityKey(_tokenFault == "signature" ? otherKey : _rsa)
				{ KeyId = "test-key" }, SecurityAlgorithms.RsaSha256)
		});
		return new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = JsonContent.Create(new
			{
				id_token = token, access_token = "access-token", refresh_token = $"refresh-{_tokenRequests.Count}",
				token_type = "Bearer", expires_in = _lifetime,
				scope = _grantPlan ? "openid email profile offline_access resource.invoke chatgpt.tokens.use.direct" : "openid email profile"
			})
		};
	}));

	private static Dictionary<string, string> ParseForm(string content) => content.Split('&')
		.Select(pair => pair.Split('=', 2)).ToDictionary(pair => WebUtility.UrlDecode(pair[0]), pair => WebUtility.UrlDecode(pair[1]));

	public void Dispose()
	{
		_rsa.Dispose();
		if (Directory.Exists(_directory))
		{
			Directory.Delete(_directory, recursive: true);
		}
	}

	private sealed class TestAuthorization(Func<IReadOnlyDictionary<string, string>, CancellationToken, Task<ChatGPTAuthorizationCode>> authorize)
		: IChatGPTAuthorization
	{
		public Task<ChatGPTAuthorizationCode> AuthorizeAsync(IReadOnlyDictionary<string, string> parameters,
			CancellationToken cancellationToken) => authorize(parameters, cancellationToken);
	}

	private sealed class TestHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
			=> respond(request);
	}
}
