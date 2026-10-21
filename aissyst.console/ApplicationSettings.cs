using aissyst.core;

namespace aissyst.console;

public class ApplicationSettings : IApplicationSettings
{
	public AudioSourceType AvailableSources { get; }
	public TimeSpan PauseUntilRecordingStops { get; }
	public float SignalThreshold { get; }

	public ApplicationSettings(TimeSpan? pauseUntilRecordingStops = null, float signalThreshold = 0.01f,
		AudioSourceType availableSources = AudioSourceType.Both)
	{
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
	}
}
