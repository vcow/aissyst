using aissyst.core;

namespace aissyst.desktop.Settings;

internal sealed record DesktopApplicationSettings : IApplicationSettings
{
	public AudioSourceType AvailableSources { get; init; } = AudioSourceType.Both;
	public TimeSpan PauseUntilRecordingStops { get; init; } = TimeSpan.FromSeconds(1);
	public TimeSpan MaxRecordingDuration { get; init; } = TimeSpan.FromSeconds(15);
	public float SignalThreshold { get; init; } = 0.01f;
	public string TranscriptionModelPath { get; init; } = Path.Combine(AppContext.BaseDirectory, "models", "ggml-small.en-q5_1.bin");
	public Uri TranscriptionModelUrl { get; init; } = new("https://huggingface.co/ggerganov/whisper.cpp/resolve/c521a4b02f422512d734391fdf08bb08c0862f68/ggml-small.en-q5_1.bin");
	public string TranscriptionModelSha256 { get; init; } = "bfdff4894dcb76bbf647d56263ea2a96645423f1669176f4844a1bf8e478ad30";
	public string TranscriptionLanguage { get; init; } = "en";
	public int TranscriptionQueueCapacity { get; init; } = 10;
	public int MaxTranscriptionProcessingCount { get; init; } = 1;
	public int MaxTextProcessingCount { get; init; } = 1;
	public bool UseMarkdownFormat { get; init; } = true;

	public DesktopApplicationSettings Validated()
	{
		if (AvailableSources is not (AudioSourceType.Both or AudioSourceType.Microphone or AudioSourceType.Application))
		{
			throw new ArgumentException("Select at least one supported audio source.");
		}
		if (PauseUntilRecordingStops <= TimeSpan.Zero || MaxRecordingDuration <= TimeSpan.Zero)
		{
			throw new ArgumentException("Recording durations must be greater than zero.");
		}
		if (!float.IsFinite(SignalThreshold) || SignalThreshold <= 0 || SignalThreshold > 1)
		{
			throw new ArgumentException("The signal threshold must be greater than zero and no greater than one.");
		}
		if (TranscriptionQueueCapacity < 1)
		{
			throw new ArgumentException("The transcription queue must hold at least one fragment.");
		}
		if (MaxTranscriptionProcessingCount is < 1 or > 5 || MaxTextProcessingCount is < 1 or > 5)
		{
			throw new ArgumentException("Processing concurrency must be between 1 and 5.");
		}
		ArgumentException.ThrowIfNullOrWhiteSpace(TranscriptionModelPath);
		ArgumentException.ThrowIfNullOrWhiteSpace(TranscriptionLanguage);
		if (TranscriptionModelUrl == null || !TranscriptionModelUrl.IsAbsoluteUri
			|| TranscriptionModelUrl.Scheme is not ("https" or "http"))
		{
			throw new ArgumentException("The model download URL must be an absolute HTTP or HTTPS URL.");
		}
		if (TranscriptionModelSha256 == null || TranscriptionModelSha256.Length != 64
			|| !TranscriptionModelSha256.All(Uri.IsHexDigit))
		{
			throw new ArgumentException("The model checksum must contain exactly 64 hexadecimal SHA-256 characters.");
		}
		return this with
		{
			TranscriptionModelPath = Path.GetFullPath(TranscriptionModelPath.Trim()),
			TranscriptionLanguage = TranscriptionLanguage.Trim()
		};
	}
}
