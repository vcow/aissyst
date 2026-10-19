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
		Assert.Null(recorder.Append(Frame(0), TimeSpan.Zero));
		Assert.Null(recorder.Append(Frame(0.001f), TimeSpan.FromSeconds(1)));
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
		Assert.NotNull(old);
		Assert.Equal(new AudioFormat(16000, 1), old.Format);
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

	private static AudioFrame Frame(float value)
	{
		return new AudioFrame(new[] { value, value }, new AudioFormat(16000, 1), TimeSpan.Zero);
	}
}
