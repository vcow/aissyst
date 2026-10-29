namespace chatgpt_connector;

public sealed class ChatGPTConnectorOptions
{
	public string ApplicationName { get; init; } = "aissyst";
	public string StorageDirectory { get; init; } = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "aissyst", "chatgpt");
	public TimeSpan ConnectionTimeout { get; init; } = TimeSpan.FromSeconds(30);
	public TimeSpan ResponseTimeout { get; init; } = TimeSpan.FromMinutes(2);
	public TimeSpan LoginTimeout { get; init; } = TimeSpan.FromMinutes(5);
}

public sealed record ChatGPTAccount(string Subject, string? Email, bool CanUseChatGPTPlan);
