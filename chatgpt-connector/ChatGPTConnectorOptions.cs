namespace chatgpt_connector;

public sealed class ChatGPTConnectorOptions
{
	public string ApplicationName { get; init; } = "aissyst";
	public string StorageDirectory { get; init; } = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "aissyst", "chatgpt");
	public TimeSpan ConnectionTimeout { get; init; } = TimeSpan.FromSeconds(30);
	public TimeSpan ResponseTimeout { get; init; } = TimeSpan.FromMinutes(2);
	public TimeSpan ConnectionLifetime { get; init; } = TimeSpan.FromMinutes(50);
	public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(2);
	public int ReconnectAttempts { get; init; } = 3;
	public TimeSpan LoginTimeout { get; init; } = TimeSpan.FromMinutes(5);
}

public sealed record ChatGPTAccount(string Subject, string? Email, bool CanUseChatGPTPlan);
