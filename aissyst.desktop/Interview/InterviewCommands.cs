using System.Windows.Input;
using Avalonia.Input;
using aissyst.core.LLMConnector.DTO;
using aissyst.desktop.Settings;
using aissyst.desktop.ViewModels;
using Microsoft.Extensions.Logging;

namespace aissyst.desktop.Interview;

internal sealed class InterviewCommand(InterviewCommands owner, string caption, RequestType? request) : ViewModel, ICommand
{
	public string Caption { get; } = caption;
	public RequestType? Request { get; } = request;
	public KeyGesture? Gesture { get; private set; }
	public string Shortcut => Gesture?.ToString() ?? "No shortcut";
	public event EventHandler? CanExecuteChanged;
	public bool CanExecute(object? parameter) => owner.CanExecute(this);
	public void Execute(object? parameter) => _ = owner.ExecuteAsync(this);
	internal void Update(string gesture)
	{
		Gesture = string.IsNullOrWhiteSpace(gesture) ? null : KeyGesture.Parse(gesture);
		Notify(nameof(Gesture));
		Notify(nameof(Shortcut));
	}
	internal void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

internal sealed class InterviewCommands : IAsyncDisposable
{
	private readonly DesktopInterview _interview;
	private readonly DesktopSettingsStore _settings;
	private readonly PromptViewModel _prompt;
	private readonly ILogger<InterviewCommands> _logger;
	private readonly CancellationTokenSource _shutdown = new();
	private readonly HashSet<Task> _pending = [];
	private bool _blocked;
	private int _requests;
	private bool _stopped;
	private Task? _disposal;
	public IReadOnlyList<InterviewCommand> Items { get; }
	public event Action<InterviewCommand>? Executing;
	public event Action? Changed;

	public InterviewCommands(DesktopInterview interview, DesktopSettingsStore settings, PromptViewModel prompt,
		ILogger<InterviewCommands> logger)
	{
		_interview = interview;
		_settings = settings;
		_prompt = prompt;
		_logger = logger;
		Items = [new(this, "Force Dialog", null), new(this, "Translate", RequestType.Translate),
			new(this, "Suggest", RequestType.Advice), new(this, "Answer", RequestType.Reply),
			new(this, "Translate & Answer", RequestType.TranslateAndReply),
			new(this, "Translate & Suggest", RequestType.TranslateAndAdvice)];
		prompt.SetCommands(Items);
		ApplySettings();
		settings.SavedChanged += SettingsChanged;
		interview.StateChanged += StateChanged;
		interview.ConnectionChanged += StateChanged;
	}

	public bool CanExecute(InterviewCommand command) => !_stopped && !_blocked && _interview.IsConnected
		&& (command.Request != null || _interview.IsStarted);

	public void SetBlocked(bool blocked)
	{
		if (_blocked != blocked)
		{
			_blocked = blocked;
			Refresh();
		}
	}

	// All entry points (native menus, buttons and routed keys) run on the UI thread.
	public Task ExecuteAsync(InterviewCommand command)
	{
		if (!CanExecute(command))
		{
			return Task.CompletedTask;
		}
		var task = RunAsync(command);
		_pending.Add(task);
		_ = RemoveWhenDoneAsync(task);
		return task;
	}

	private async Task RemoveWhenDoneAsync(Task task)
	{
		await task;
		_pending.Remove(task);
	}

	private async Task RunAsync(InterviewCommand command)
	{
		var isRequest = command.Request != null;
		if (isRequest)
		{
			_requests++;
			_prompt.SetProcessing(true);
		}
		try
		{
			Executing?.Invoke(command);
			if (command.Request is { } request)
			{
				await _interview.SendRequestAsync(request, _shutdown.Token);
			}
			else
			{
				await _interview.ForceDialogAsync(_shutdown.Token);
			}
		}
		catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
		catch (Exception exception)
		{
			_logger.LogError(exception, "Failed to execute {Command}.", command.Caption);
		}
		finally
		{
			if (isRequest)
			{
				_requests--;
				_prompt.SetProcessing(_requests > 0);
			}
		}
	}

	public Task WaitForIdleAsync() => Task.WhenAll(_pending.ToArray());
	private void SettingsChanged() => PromptViewModel.OnUI(ApplySettings);
	private void ApplySettings()
	{
		if (_stopped) { return; }
		var settings = _settings.Saved.Prompt;
		_prompt.Apply(settings);
		for (var index = 0; index < Items.Count; index++)
		{
			Items[index].Update(settings.Gestures[index]);
		}
		Refresh();
	}
	private void StateChanged() => PromptViewModel.OnUI(Refresh);
	private void Refresh()
	{
		foreach (var command in Items) { command.Refresh(); }
		Changed?.Invoke();
	}
	public void Stop()
	{
		if (_stopped) { return; }
		_stopped = true;
		_shutdown.Cancel();
		Refresh();
	}
	public ValueTask DisposeAsync() => new(_disposal ??= DisposeCoreAsync());
	private async Task DisposeCoreAsync()
	{
		Stop();
		_settings.SavedChanged -= SettingsChanged;
		_interview.StateChanged -= StateChanged;
		_interview.ConnectionChanged -= StateChanged;
		await WaitForIdleAsync();
		_shutdown.Dispose();
	}
}
