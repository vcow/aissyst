using System.Runtime.InteropServices;
using aissyst.core;
using aissyst.core.Audio;

namespace aissyst.console;

internal sealed class AudioRecorder(IApplicationSettings settings) : IDisposable
{
	private readonly MemoryStream _samples = new();
	private AudioFormat? _format;
	private TimeSpan _lastSignalAt;
	private long _lastSignalLength;

	internal IReadOnlyList<AudioRecording> Append(AudioFrame frame, TimeSpan now)
	{
		if (frame.Format.SampleRate <= 0 || frame.Format.Channels <= 0
			|| frame.Format.Channels > int.MaxValue / sizeof(float)
			|| frame.Samples.Length % frame.Format.Channels != 0)
		{
			throw new InvalidOperationException("Invalid PCM audio frame.");
		}

		foreach (var sample in frame.Samples.Span)
		{
			if (!float.IsFinite(sample))
			{
				throw new InvalidOperationException("The audio stream contains non-finite samples.");
			}
		}

		var recordings = new List<AudioRecording>();
		var previous = _format.HasValue && _format != frame.Format ? Complete() : CompleteIfSilent(now);
		if (previous != null)
		{
			recordings.Add(previous);
		}

		// Count PCM frames, not wall-clock time. Boundaries always contain all channels.
		var durationFrames = decimal.Floor((decimal)settings.MaxRecordingDuration.Ticks
			* frame.Format.SampleRate / TimeSpan.TicksPerSecond);
		var maximumFrames = Math.Clamp(durationFrames, 1, int.MaxValue / sizeof(float) / frame.Format.Channels);
		var maximumSamples = (int)maximumFrames * frame.Format.Channels;
		var offset = 0;
		while (offset < frame.Samples.Length)
		{
			var remainingCapacity = maximumSamples - (int)(_samples.Length / sizeof(float));
			var count = Math.Min(remainingCapacity, frame.Samples.Length - offset);
			AppendSamples(frame.Samples.Span.Slice(offset, count), frame.Format, now);
			offset += count;
			if (_samples.Length / sizeof(float) == maximumSamples)
			{
				if (Complete() is { } recording)
				{
					recordings.Add(recording);
				}
			}
		}
		return recordings;
	}

	private void AppendSamples(ReadOnlySpan<float> samples, AudioFormat format, TimeSpan now)
	{
		double energy = 0;
		foreach (var sample in samples)
		{
			energy += (double)sample * sample;
		}
		var hasSignal = energy / samples.Length >= (double)settings.SignalThreshold * settings.SignalThreshold;
		if (!hasSignal && _samples.Length == 0)
		{
			return;
		}
		_format = format;
		_samples.Write(MemoryMarshal.AsBytes(samples));
		if (hasSignal)
		{
			_lastSignalAt = now;
			_lastSignalLength = _samples.Length;
		}
	}

	internal AudioRecording? CompleteIfSilent(TimeSpan now)
	{
		return _samples.Length > 0 && now - _lastSignalAt >= settings.PauseUntilRecordingStops
			? Complete() : null;
	}

	internal AudioRecording? Complete()
	{
		if (_samples.Length == 0 || !_format.HasValue)
		{
			return null;
		}

		// Keep pauses inside a recording, but remove trailing silence from the completed fragment.
		var data = MemoryMarshal.Cast<byte, float>(_samples.GetBuffer().AsSpan(0, checked((int)_lastSignalLength))).ToArray();
		var recording = new AudioRecording(data, _format.Value);
		_samples.SetLength(0);
		_samples.Position = 0;
		_lastSignalLength = 0;
		_format = null;
		return recording;
	}

	public void Dispose()
	{
		_samples.Dispose();
	}
}
