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

	internal AudioRecording? Append(AudioFrame frame, TimeSpan now)
	{
		if (frame.Format.SampleRate <= 0 || frame.Format.Channels <= 0
			|| frame.Samples.Length % frame.Format.Channels != 0)
		{
			throw new InvalidOperationException("Invalid PCM audio frame.");
		}

		var completed = _format.HasValue && _format != frame.Format ? Complete() : CompleteIfSilent(now);
		double energy = 0;
		foreach (var sample in frame.Samples.Span)
		{
			if (!float.IsFinite(sample))
			{
				throw new InvalidOperationException("The audio stream contains non-finite samples.");
			}
			energy += (double)sample * sample;
		}
		var hasSignal = frame.Samples.Length > 0
			&& energy / frame.Samples.Length >= (double)settings.SignalThreshold * settings.SignalThreshold;
		if (!hasSignal && _samples.Length == 0)
		{
			return completed;
		}

		_format = frame.Format;
		_samples.Write(MemoryMarshal.AsBytes(frame.Samples.Span));
		if (hasSignal)
		{
			_lastSignalAt = now;
			_lastSignalLength = _samples.Length;
		}
		return completed;
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
