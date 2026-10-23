using aissyst.core.Audio;
using aissyst.core.Transcription;

namespace aissyst.tests;

public class WhisperAudioConverterTests
{
	[Fact]
	public void StereoFramesAreJoinedAndMixedToMono()
	{
		AudioFrame[] frames =
		[
			new(new float[] { 1, 0, -1, 0 }, new AudioFormat(16000, 2), TimeSpan.Zero),
			new(new float[] { 0.5f, 0.5f }, new AudioFormat(16000, 2), TimeSpan.FromSeconds(1))
		];
		Assert.Equal(new float[] { 0.5f, -0.5f, 0.5f }, WhisperAudioConverter.Convert(frames, CancellationToken.None));
	}

	[Theory]
	[InlineData(44100)]
	[InlineData(48000)]
	[InlineData(8000)]
	public void ResamplingPreservesSpeechBandToneAndDuration(int sampleRate)
	{
		var input = Enumerable.Range(0, sampleRate).Select(index => (float)Math.Sin(2 * Math.PI * 1000 * index / sampleRate)).ToArray();
		var output = WhisperAudioConverter.Convert([new(input, new AudioFormat(sampleRate, 1), TimeSpan.Zero)], CancellationToken.None);
		Assert.Equal(16000, output.Length);
		var error = output.Skip(100).Take(15800).Select((sample, index) =>
			Math.Pow(sample - Math.Sin(2 * Math.PI * 1000 * (index + 100) / 16000), 2)).Average();
		Assert.InRange(Math.Sqrt(error), 0, 0.02);
	}

	[Fact]
	public void DownsamplingFiltersFrequenciesAboveTheTargetNyquistLimit()
	{
		var input = Enumerable.Range(0, 48000).Select(index => (float)Math.Sin(2 * Math.PI * 12000 * index / 48000)).ToArray();
		var output = WhisperAudioConverter.Convert([new(input, new AudioFormat(48000, 1), TimeSpan.Zero)], CancellationToken.None);
		var rms = Math.Sqrt(output.Skip(100).Take(15800).Average(sample => (double)sample * sample));
		Assert.InRange(rms, 0, 0.01);
	}

	[Fact]
	public void InvalidSamplesAndMixedFormatsAreRejected()
	{
		Assert.Throws<ArgumentException>(() => WhisperAudioConverter.Convert(
			[new(new float[] { float.NaN }, new AudioFormat(16000, 1), TimeSpan.Zero)], CancellationToken.None));
		Assert.Throws<ArgumentException>(() => WhisperAudioConverter.Convert(
			[new(new float[] { 0 }, new AudioFormat(16000, 2), TimeSpan.Zero)], CancellationToken.None));
		Assert.Throws<ArgumentException>(() => WhisperAudioConverter.Convert(
			[new(new float[] { 0 }, new AudioFormat(16000, 1), TimeSpan.Zero),
			new(new float[] { 0 }, new AudioFormat(48000, 1), TimeSpan.Zero)], CancellationToken.None));
	}

	[Fact]
	public void CancellationStopsAudioPreparation()
	{
		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();
		Assert.ThrowsAny<OperationCanceledException>(() => WhisperAudioConverter.Convert([], cancellation.Token));
	}
}
