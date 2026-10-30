using aissyst.core;
using aissyst.desktop.Settings;

namespace aissyst.desktop.ViewModels;

internal sealed record AudioSourceChoice(AudioSourceType Value, string Label);

internal sealed class SettingsViewModel : ViewModel
{
	private double _transcriptionConcurrency;
	private double _textConcurrency;
	public IReadOnlyList<AudioSourceChoice> SourceChoices { get; } =
	[
		new(AudioSourceType.Both, "Microphone and application"),
		new(AudioSourceType.Microphone, "Microphone only"),
		new(AudioSourceType.Application, "Application only")
	];
	public AudioSourceChoice? SelectedSource { get; set; }
	public decimal? SilenceSeconds { get; set; }
	public decimal? FragmentSeconds { get; set; }
	public decimal? SignalThreshold { get; set; }
	public decimal? QueueCapacity { get; set; }
	public double TranscriptionConcurrency
	{
		get => _transcriptionConcurrency;
		set
		{
			_transcriptionConcurrency = value;
			Notify();
		}
	}
	public double TextConcurrency
	{
		get => _textConcurrency;
		set
		{
			_textConcurrency = value;
			Notify();
		}
	}
	public string ModelPath { get; set; } = string.Empty;
	public string ModelUrl { get; set; } = string.Empty;
	public string ModelSha256 { get; set; } = string.Empty;
	public string Language { get; set; } = string.Empty;
	public bool UseMarkdown { get; set; }
	public string Error { get; private set; } = string.Empty;
	public bool HasError => Error.Length > 0;
	public bool IsEditing { get; private set; } = true;

	public SettingsViewModel(DesktopApplicationSettings settings) => Load(settings);

	private void Load(DesktopApplicationSettings settings)
	{
		SelectedSource = SourceChoices.First(choice => choice.Value == settings.AvailableSources);
		SilenceSeconds = (decimal)settings.PauseUntilRecordingStops.TotalSeconds;
		FragmentSeconds = (decimal)settings.MaxRecordingDuration.TotalSeconds;
		SignalThreshold = (decimal)settings.SignalThreshold;
		QueueCapacity = settings.TranscriptionQueueCapacity;
		TranscriptionConcurrency = settings.MaxTranscriptionProcessingCount;
		TextConcurrency = settings.MaxTextProcessingCount;
		ModelPath = settings.TranscriptionModelPath;
		ModelUrl = settings.TranscriptionModelUrl.AbsoluteUri;
		ModelSha256 = settings.TranscriptionModelSha256;
		Language = settings.TranscriptionLanguage;
		UseMarkdown = settings.UseMarkdownFormat;
		SetError(string.Empty);
		foreach (var property in GetType().GetProperties())
		{
			Notify(property.Name);
		}
	}

	public void RestoreDefaults() => Load(new DesktopApplicationSettings());

	public void SetModelPath(string path)
	{
		ModelPath = path;
		Notify(nameof(ModelPath));
	}

	public void SetEditing(bool editing)
	{
		IsEditing = editing;
		Notify(nameof(IsEditing));
	}

	public void SetError(string error)
	{
		Error = error;
		Notify(nameof(Error));
		Notify(nameof(HasError));
	}

	public DesktopApplicationSettings GetSettings()
	{
		if (SelectedSource == null || SilenceSeconds == null || FragmentSeconds == null
			|| SignalThreshold == null || QueueCapacity == null)
		{
			throw new ArgumentException("Fill in all numeric fields and select the audio sources.");
		}
		if (QueueCapacity != decimal.Truncate(QueueCapacity.Value))
		{
			throw new ArgumentException("The queue capacity must be a whole number.");
		}
		if (!double.IsFinite(TranscriptionConcurrency) || !double.IsFinite(TextConcurrency)
			|| TranscriptionConcurrency != Math.Truncate(TranscriptionConcurrency)
			|| TextConcurrency != Math.Truncate(TextConcurrency))
		{
			throw new ArgumentException("Processing concurrency must be a whole number between 1 and 5.");
		}
		if (!Uri.TryCreate(ModelUrl?.Trim(), UriKind.Absolute, out var url))
		{
			throw new ArgumentException("Enter a valid model download URL.");
		}
		return new DesktopApplicationSettings
		{
			AvailableSources = SelectedSource.Value,
			PauseUntilRecordingStops = TimeSpan.FromTicks(checked((long)(SilenceSeconds.Value * TimeSpan.TicksPerSecond))),
			MaxRecordingDuration = TimeSpan.FromTicks(checked((long)(FragmentSeconds.Value * TimeSpan.TicksPerSecond))),
			SignalThreshold = (float)SignalThreshold.Value,
			TranscriptionQueueCapacity = checked((int)QueueCapacity.Value),
			MaxTranscriptionProcessingCount = checked((int)TranscriptionConcurrency),
			MaxTextProcessingCount = checked((int)TextConcurrency),
			TranscriptionModelPath = ModelPath, TranscriptionModelUrl = url,
			TranscriptionModelSha256 = ModelSha256?.Trim()!, TranscriptionLanguage = Language,
			UseMarkdownFormat = UseMarkdown
		}.Validated();
	}
}
