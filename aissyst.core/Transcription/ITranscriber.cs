using aissyst.core.Audio;

namespace aissyst.core.Transcription;

public interface ITranscriber : IAsyncDisposable
{
	bool IsReady { get; }
	Task InitializeAsync(CancellationToken cancellationToken = default);
	Task<string> TranscribeAsync(IReadOnlyList<AudioFrame> frames, CancellationToken cancellationToken = default);
}
