using aissyst.core.Audio;

namespace aissyst.core.Transcription;

internal static class WhisperAudioConverter
{
	internal const int SampleRate = 16000;

	internal static float[] Convert(IReadOnlyList<AudioFrame> frames, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(frames);
		cancellationToken.ThrowIfCancellationRequested();
		if (frames.Count == 0)
		{
			return [];
		}
		var format = frames[0].Format;
		if (format.SampleRate <= 0 || format.Channels <= 0)
		{
			throw new ArgumentException("The audio format must have a positive sample rate and channel count.", nameof(frames));
		}
		var sampleCount = 0;
		foreach (var frame in frames)
		{
			if (frame.Format != format || frame.Samples.Length % format.Channels != 0)
			{
				throw new ArgumentException("All audio frames must contain complete samples in the same format.", nameof(frames));
			}
			sampleCount = checked(sampleCount + frame.Samples.Length / format.Channels);
		}
		var mono = new float[sampleCount];
		var offset = 0;
		foreach (var frame in frames)
		{
			var samples = frame.Samples.Span;
			for (var index = 0; index < samples.Length; index += format.Channels)
			{
				if ((offset & 4095) == 0)
				{
					cancellationToken.ThrowIfCancellationRequested();
				}
				double sum = 0;
				for (var channel = 0; channel < format.Channels; channel++)
				{
					var sample = samples[index + channel];
					if (!float.IsFinite(sample))
					{
						throw new ArgumentException("Audio samples must be finite.", nameof(frames));
					}
					sum += sample;
				}
				mono[offset++] = (float)(sum / format.Channels);
			}
		}
		if (format.SampleRate == SampleRate || mono.Length == 0)
		{
			return mono;
		}

		var output = new float[checked((int)Math.Round((double)mono.Length * SampleRate / format.SampleRate))];
		const int radius = 32;
		var cutoff = Math.Min(1.0, (double)SampleRate / format.SampleRate) * 0.94;
		for (var index = 0; index < output.Length; index++)
		{
			if ((index & 255) == 0)
			{
				cancellationToken.ThrowIfCancellationRequested();
			}
			var position = (double)index * format.SampleRate / SampleRate;
			var center = (int)position;
			double value = 0, weights = 0;
			// Windowed-sinc low-pass filtering prevents aliasing when downsampling captured audio.
			for (var source = Math.Max(0, center - radius + 1); source <= Math.Min(mono.Length - 1, center + radius); source++)
			{
				var distance = position - source;
				var argument = Math.PI * cutoff * distance;
				var sinc = Math.Abs(argument) < 1e-10 ? 1 : Math.Sin(argument) / argument;
				var weight = cutoff * sinc * (0.5 + 0.5 * Math.Cos(Math.PI * distance / radius));
				value += mono[source] * weight;
				weights += weight;
			}
			output[index] = weights == 0 ? 0 : (float)(value / weights);
		}
		return output;
	}
}
