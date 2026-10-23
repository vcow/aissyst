using System.Runtime.CompilerServices;
using System.Threading.Channels;
using aissyst.core.Audio;

namespace aissyst.tests;

internal sealed class TestAudioCapture : IAudioCapture
{
	private readonly Channel<Emission> _microphone = Channel.CreateUnbounded<Emission>();
	private readonly Channel<Emission> _application = Channel.CreateUnbounded<Emission>();
	internal int MicrophoneEnumerations { get; private set; }
	internal int ApplicationEnumerations { get; private set; }
	internal int MicrophoneCaptures { get; private set; }
	internal int ApplicationCaptures { get; private set; }

	internal Task EmitMicrophoneAsync(AudioFrame frame) => EmitAsync(_microphone, frame);
	internal Task EmitApplicationAsync(AudioFrame frame) => EmitAsync(_application, frame);

	public Task<IReadOnlyList<AudioInputDevice>> GetInputDevicesAsync(CancellationToken cancellationToken = default)
	{
		MicrophoneEnumerations++;
		return Task.FromResult<IReadOnlyList<AudioInputDevice>>([new("test-microphone", "Test microphone", 1, true)]);
	}

	public Task<IReadOnlyList<AudioApplication>> GetApplicationsAsync(CancellationToken cancellationToken = default)
	{
		ApplicationEnumerations++;
		return Task.FromResult<IReadOnlyList<AudioApplication>>([new(123, "Test application", true, [123])]);
	}

	public IAsyncEnumerable<AudioFrame> CaptureMicrophoneAsync(string deviceId, CancellationToken cancellationToken = default)
	{
		MicrophoneCaptures++;
		return ReadAsync(_microphone, cancellationToken);
	}

	public IAsyncEnumerable<AudioFrame> CaptureApplicationAudioAsync(AudioApplication application, CancellationToken cancellationToken = default)
	{
		ApplicationCaptures++;
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
