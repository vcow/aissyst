namespace aissyst.console;

public class ApplicationSettings
{
	public TimeSpan PauseUntilRecordingStops { get; }
	public float SignalThreshold { get; }
	public string RecordingDirectory { get; }

	public ApplicationSettings(TimeSpan? pauseUntilRecordingStops = null, float signalThreshold = 0.01f,
		string? recordingDirectory = null)
	{
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
		RecordingDirectory = Path.GetFullPath(recordingDirectory ?? "recordings");
	}
}
