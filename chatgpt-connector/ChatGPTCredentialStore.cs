using System.Text.Json;

namespace chatgpt_connector;

internal sealed class ChatGPTCredentials
{
	public string HostId { get; set; } = "";
	public string? PendingClientId { get; set; }
	public ChatGPTProfile? Profile { get; set; }
}

internal sealed class ChatGPTProfile
{
	public required string ClientId { get; init; }
	public required string Subject { get; init; }
	public string? Email { get; init; }
	public required string IdToken { get; init; }
	public required string AccessToken { get; init; }
	public string? RefreshToken { get; init; }
	public required string[] Scopes { get; init; }
	public DateTimeOffset ExpiresAt { get; init; }
}

internal sealed class ChatGPTCredentialStore(string directory)
{
	private readonly string _path = Path.Combine(directory, "credentials.json");

	public FileStream AcquireLock()
	{
		Directory.CreateDirectory(directory);
		if (!OperatingSystem.IsWindows())
		{
			File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		}
		try
		{
			return OpenPrivateFile(Path.Combine(directory, "session.lock"), FileMode.OpenOrCreate);
		}
		catch (IOException)
		{
			throw new IOException("The ChatGPT session is in use by another aissyst instance.");
		}
	}

	public async Task<ChatGPTCredentials> LoadAsync(CancellationToken cancellationToken)
	{
		if (!File.Exists(_path))
		{
			return new ChatGPTCredentials { HostId = $"urn:uuid:{Guid.NewGuid():D}" };
		}
		using var stream = OpenPrivateFile(_path, FileMode.Open);
		var credentials = await JsonSerializer.DeserializeAsync<ChatGPTCredentials>(stream, cancellationToken: cancellationToken)
			?? throw new InvalidDataException("The saved ChatGPT credentials are empty.");
		if (!Uri.TryCreate(credentials.HostId, UriKind.Absolute, out var hostId) || hostId.Scheme != "urn")
		{
			throw new InvalidDataException("The saved ChatGPT host identifier is invalid.");
		}
		return credentials;
	}

	public async Task SaveAsync(ChatGPTCredentials credentials, CancellationToken cancellationToken)
	{
		var temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			await using (var stream = OpenPrivateFile(temporaryPath, FileMode.CreateNew))
			{
				await JsonSerializer.SerializeAsync(stream, credentials, cancellationToken: cancellationToken);
				await stream.FlushAsync(cancellationToken);
			}
			File.Move(temporaryPath, _path, overwrite: true);
		}
		finally
		{
			File.Delete(temporaryPath);
		}
	}

	private static FileStream OpenPrivateFile(string path, FileMode mode)
	{
		var options = new FileStreamOptions { Mode = mode, Access = FileAccess.ReadWrite, Share = FileShare.None };
		if (!OperatingSystem.IsWindows() && mode != FileMode.Open)
		{
			options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
		}
		var stream = new FileStream(path, options);
		if (!OperatingSystem.IsWindows())
		{
			File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
		}
		return stream;
	}
}
