using aissyst.core.Audio;

namespace aissyst.console;

public sealed class CaptureSessionContext
{
	private AudioInputDevice? _microphone;
	private AudioApplication? _application;

	public AudioInputDevice Microphone => _microphone
		?? throw new InvalidOperationException("The session context has not been initialized.");
	public AudioApplication Application => _application
		?? throw new InvalidOperationException("The session context has not been initialized.");

	public void Initialize(AudioInputDevice microphone, AudioApplication application)
	{
		ArgumentNullException.ThrowIfNull(microphone);
		ArgumentNullException.ThrowIfNull(application);
		if (_microphone != null)
			throw new InvalidOperationException("The session context has already been initialized.");

		_microphone = microphone;
		_application = application;
	}
}
