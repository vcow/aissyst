namespace aissyst.core.LLMConnector.Model;

public sealed class SessionRecoveryOptions
{
	public TimeSpan Delay { get; init; } = TimeSpan.FromSeconds(2);
	public int Attempts { get; init; } = 3;
}
