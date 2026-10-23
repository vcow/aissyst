using aissyst.core;

namespace aissyst.console;

public class ApplicationSettings : IApplicationSettings
{
	public AudioSourceType AvailableSources { get; }
	public TimeSpan PauseUntilRecordingStops { get; }
	public float SignalThreshold { get; }
	public string TranscriptionModelPath { get; }
	public Uri TranscriptionModelUrl { get; }
	public string TranscriptionModelSha256 { get; }
	public string TranscriptionLanguage { get; }
	public int TranscriptionQueueCapacity { get; }
	public int MaxTranscriptionProcessingCount { get; }
	public int MaxTextProcessingCount { get; }

	public ApplicationSettings(TimeSpan? pauseUntilRecordingStops = null, float signalThreshold = 0.01f,
		AudioSourceType availableSources = AudioSourceType.Both,
		string? transcriptionModelPath = null, Uri? transcriptionModelUrl = null,
		string transcriptionModelSha256 = "bfdff4894dcb76bbf647d56263ea2a96645423f1669176f4844a1bf8e478ad30",
		string transcriptionLanguage = "en", int transcriptionQueueCapacity = 10,
		int maxTranscriptionProcessingCount = 1, int maxTextProcessingCount = 1)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTranscriptionProcessingCount);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTextProcessingCount);
		if (transcriptionQueueCapacity <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(transcriptionQueueCapacity), "The transcription queue capacity must be positive.");
		}
		TranscriptionQueueCapacity = transcriptionQueueCapacity;
		ArgumentException.ThrowIfNullOrWhiteSpace(transcriptionLanguage);
		ArgumentException.ThrowIfNullOrWhiteSpace(transcriptionModelSha256);
		if (transcriptionModelSha256.Length != 64 || !transcriptionModelSha256.All(Uri.IsHexDigit))
		{
			throw new ArgumentException("The model checksum must be a SHA-256 hex string.", nameof(transcriptionModelSha256));
		}
		TranscriptionModelPath = Path.GetFullPath(transcriptionModelPath
			?? Path.Combine(AppContext.BaseDirectory, "models", "ggml-small.en-q5_1.bin"));
		TranscriptionModelUrl = transcriptionModelUrl ?? new Uri(
			"https://huggingface.co/ggerganov/whisper.cpp/resolve/c521a4b02f422512d734391fdf08bb08c0862f68/ggml-small.en-q5_1.bin");
		TranscriptionModelSha256 = transcriptionModelSha256;
		TranscriptionLanguage = transcriptionLanguage;
		AvailableSources = availableSources;
		PauseUntilRecordingStops = pauseUntilRecordingStops ?? TimeSpan.FromSeconds(1);
		if (PauseUntilRecordingStops <= TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(pauseUntilRecordingStops));
		}
		if (!float.IsFinite(signalThreshold) || signalThreshold <= 0 || signalThreshold > 1)
		{
			throw new ArgumentOutOfRangeException(nameof(signalThreshold));
		}
		SignalThreshold = signalThreshold;
		MaxTranscriptionProcessingCount = maxTranscriptionProcessingCount;
		MaxTextProcessingCount = maxTextProcessingCount;
	}
}
