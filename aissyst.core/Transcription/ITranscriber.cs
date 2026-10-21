namespace aissyst.core.Transcription;

public interface ITranscriber : IAsyncDisposable
{
	bool IsReady { get; }
	Task InitializeAsync(CancellationToken cancellationToken = default);
}
