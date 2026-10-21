using aissyst.core.Audio;

namespace aissyst.console;

public sealed class CaptureSessionContext
{
	private AudioInputDevice? _microphone;
	private AudioApplication? _application;
	internal bool IsInitialized { get; private set; }

	public AudioInputDevice? Microphone => IsInitialized ? _microphone
		: throw new InvalidOperationException("The session context has not been initialized.");
	public AudioApplication? Application => IsInitialized ? _application
		: throw new InvalidOperationException("The session context has not been initialized.");

	public void Initialize(AudioInputDevice? microphone, AudioApplication? application)
	{
		if (IsInitialized)
		{
			throw new InvalidOperationException("The session context has already been initialized.");
		}
		if (microphone == null && application == null)
		{
			throw new ArgumentException("At least one audio source must be selected.");
		}

		_microphone = microphone;
		_application = application;
		IsInitialized = true;
	}
}
