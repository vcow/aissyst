using System.Buffers.Binary;
using aissyst.console;
using aissyst.core.Audio;

namespace aissyst.tests;

public class AudioRecorderTests
{
	[Fact]
	public void SilenceDoesNotStartRecordingAndManualCompletionDoesNotCreateEmptyRecords()
	{
		using var recorder = new AudioRecorder(new ApplicationSettings(signalThreshold: 0.01f));
		Assert.Empty(recorder.Append(Frame(0), TimeSpan.Zero));
		Assert.Empty(recorder.Append(Frame(0.001f), TimeSpan.FromSeconds(1)));
		Assert.Null(recorder.CompleteIfSilent(TimeSpan.FromSeconds(2)));
		Assert.Null(recorder.Complete());
	}

	[Fact]
	public void SpeechResetsSilenceTimeoutAndTrailingSilenceIsTrimmed()
	{
		using var recorder = new AudioRecorder(new ApplicationSettings(TimeSpan.FromSeconds(1)));
		recorder.Append(Frame(0.5f), TimeSpan.Zero);
		recorder.Append(Frame(0), TimeSpan.FromMilliseconds(100));
		recorder.Append(Frame(0.25f), TimeSpan.FromMilliseconds(500));
		recorder.Append(Frame(0), TimeSpan.FromMilliseconds(600));
		Assert.Null(recorder.CompleteIfSilent(TimeSpan.FromMilliseconds(1499)));
		var recording = recorder.CompleteIfSilent(TimeSpan.FromMilliseconds(1500));
		Assert.NotNull(recording);
		Assert.Equal(new[] { 0.5f, 0.5f, 0f, 0f, 0.25f, 0.25f }, recording.Samples.ToArray());
		Assert.Null(recorder.Complete());
	}

	[Fact]
	public void FormatChangeEndsOldRecordingAndStartsANewOne()
	{
		using var recorder = new AudioRecorder(new ApplicationSettings());
		recorder.Append(Frame(0.5f), TimeSpan.Zero);
		var old = recorder.Append(new AudioFrame(new[] { 0.2f, -0.2f }, new AudioFormat(48000, 2), TimeSpan.Zero), TimeSpan.FromMilliseconds(20));
		Assert.Equal(new AudioFormat(16000, 1), Assert.Single(old).Format);
		Assert.Equal(new AudioFormat(48000, 2), recorder.Complete()!.Format);
	}

	[Fact]
	public async Task WaveWriterPreservesRateChannelsAndPcmSampleValues()
	{
		var directory = Path.Combine(Path.GetTempPath(), $"aissyst-wave-test-{Guid.NewGuid():N}");
		Directory.CreateDirectory(directory);
		try
		{
			var path = Path.Combine(directory, "audio.wav");
			await WaveFile.WriteAsync(path, new AudioRecording(new[] { -1f, 1f, 0f, 0.5f }, new AudioFormat(48000, 2)));
			var bytes = await File.ReadAllBytesAsync(path);
			Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
			Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(bytes, 8, 4));
			Assert.Equal(48000, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(24)));
			Assert.Equal(2, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(22)));
			Assert.Equal(16, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(34)));
			Assert.Equal(8, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(40)));
			Assert.Equal(short.MinValue, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(44)));
			Assert.Equal(short.MaxValue, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(46)));
			Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(48)));
			Assert.Equal(16384, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(50)));
		}
		finally
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	[Fact]
	public void DefaultLimitSplitsThirtySecondsOfPcmRegardlessOfArrivalTime()
	{
		var settings = new ApplicationSettings(pauseUntilRecordingStops: TimeSpan.FromMinutes(1));
		Assert.Equal(TimeSpan.FromSeconds(30), settings.MaxRecordingDuration);
		using var recorder = new AudioRecorder(settings);
		var format = new AudioFormat(10, 1);
		Assert.Empty(recorder.Append(new AudioFrame(Enumerable.Repeat(0.5f, 299).ToArray(), format, TimeSpan.Zero), TimeSpan.Zero));
		var recording = Assert.Single(recorder.Append(new AudioFrame(new[] { 0.25f }, format, TimeSpan.Zero), TimeSpan.Zero));
		Assert.Equal(300, recording.Samples.Length);
		Assert.Equal(0.25f, recording.Samples.Span[^1]);
		Assert.Null(recorder.Complete());
	}

	[Theory]
	[InlineData(1)]
	[InlineData(2)]
	public void LargeFrameIsSplitAtChannelAlignedBoundariesWithoutDroppingOrDuplicatingSignal(int channels)
	{
		using var recorder = new AudioRecorder(new ApplicationSettings(maxRecordingDuration: TimeSpan.FromSeconds(1)));
		var format = new AudioFormat(10, channels);
		var samples = Enumerable.Range(1, 35 * channels).Select(index => index / 100f).ToArray();
		var records = recorder.Append(new AudioFrame(samples, format, TimeSpan.Zero), TimeSpan.Zero).ToList();
		Assert.Equal(3, records.Count);
		Assert.All(records, record =>
		{
			Assert.Equal(format, record.Format);
			Assert.Equal(10 * channels, record.Samples.Length);
		});
		records.Add(Assert.IsType<AudioRecording>(recorder.Complete()));
		Assert.Equal(5 * channels, records[^1].Samples.Length);
		Assert.Equal(samples, records.SelectMany(record => record.Samples.ToArray()).ToArray());
	}

	[Fact]
	public void OversizedFrameCanCompletePreviousRecordingAndEmitMultipleNewFragments()
	{
		using var recorder = new AudioRecorder(new ApplicationSettings(maxRecordingDuration: TimeSpan.FromSeconds(1)));
		Assert.Empty(recorder.Append(Frame(0.5f), TimeSpan.Zero));
		var format = new AudioFormat(10, 2);
		var samples = Enumerable.Repeat(0.25f, 40).ToArray();
		var records = recorder.Append(new AudioFrame(samples, format, TimeSpan.Zero), TimeSpan.FromMilliseconds(1));
		Assert.Equal(3, records.Count);
		Assert.Equal(new AudioFormat(16000, 1), records[0].Format);
		Assert.Equal(samples, records.Skip(1).SelectMany(record => record.Samples.ToArray()).ToArray());
		Assert.Null(recorder.Complete());
	}

	[Fact]
	public void SilenceAndManualCompletionStillWorkAfterDurationLimit()
	{
		using var recorder = new AudioRecorder(new ApplicationSettings(maxRecordingDuration: TimeSpan.FromSeconds(1)));
		var format = new AudioFormat(10, 1);
		Assert.Single(recorder.Append(new AudioFrame(Enumerable.Repeat(0.5f, 10).ToArray(), format, TimeSpan.Zero), TimeSpan.Zero));
		Assert.Empty(recorder.Append(new AudioFrame(new[] { 0f, 0f }, format, TimeSpan.Zero), TimeSpan.FromMilliseconds(100)));
		Assert.Null(recorder.CompleteIfSilent(TimeSpan.FromSeconds(2)));
		Assert.Empty(recorder.Append(new AudioFrame(new[] { 0.25f, 0.25f }, format, TimeSpan.Zero), TimeSpan.FromSeconds(3)));
		Assert.NotNull(recorder.CompleteIfSilent(TimeSpan.FromSeconds(4)));
		Assert.Empty(recorder.Append(new AudioFrame(new[] { 0.5f }, format, TimeSpan.Zero), TimeSpan.FromSeconds(5)));
		Assert.NotNull(recorder.Complete());
		Assert.Null(recorder.Complete());
	}

	[Theory]
	[InlineData(0)]
	[InlineData(-1)]
	public void NonPositiveDurationLimitIsRejected(int seconds)
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => new ApplicationSettings(maxRecordingDuration: TimeSpan.FromSeconds(seconds)));
	}

	private static AudioFrame Frame(float value)
	{
		return new AudioFrame(new[] { value, value }, new AudioFormat(16000, 1), TimeSpan.Zero);
	}
}
