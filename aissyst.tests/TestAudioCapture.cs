using System.Runtime.CompilerServices;
using System.Threading.Channels;
using aissyst.core.Audio;

namespace aissyst.tests;

internal sealed class TestAudioCapture : IAudioCapture
{
	private readonly Channel<Emission> _microphone = Channel.CreateUnbounded<Emission>();
	private readonly Channel<Emission> _application = Channel.CreateUnbounded<Emission>();

	internal Task EmitMicrophoneAsync(AudioFrame frame) => EmitAsync(_microphone, frame);
	internal Task EmitApplicationAsync(AudioFrame frame) => EmitAsync(_application, frame);

	public Task<IReadOnlyList<AudioInputDevice>> GetInputDevicesAsync(CancellationToken cancellationToken = default)
	{
		return Task.FromResult<IReadOnlyList<AudioInputDevice>>([]);
	}

	public Task<IReadOnlyList<AudioApplication>> GetApplicationsAsync(CancellationToken cancellationToken = default)
	{
		return Task.FromResult<IReadOnlyList<AudioApplication>>([]);
	}

	public IAsyncEnumerable<AudioFrame> CaptureMicrophoneAsync(string deviceId, CancellationToken cancellationToken = default)
	{
		return ReadAsync(_microphone, cancellationToken);
	}

	public IAsyncEnumerable<AudioFrame> CaptureApplicationAudioAsync(AudioApplication application, CancellationToken cancellationToken = default)
	{
		return ReadAsync(_application, cancellationToken);
	}

	public IAsyncEnumerable<AudioFrame> CaptureSystemAudioAsync(CancellationToken cancellationToken = default)
	{
		return ReadAsync(_application, cancellationToken);
	}

	private static async Task EmitAsync(Channel<Emission> channel, AudioFrame frame)
	{
		var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		await channel.Writer.WriteAsync(new Emission(frame, queued));
		await queued.Task.WaitAsync(TimeSpan.FromSeconds(5));
	}

	private static async IAsyncEnumerable<AudioFrame> ReadAsync(Channel<Emission> channel,
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken))
		{
			yield return item.Frame;
			// The consumer requests the next item only after queuing this frame in the session.
			item.Queued.TrySetResult();
		}
	}

	private sealed record Emission(AudioFrame Frame, TaskCompletionSource Queued);
}
