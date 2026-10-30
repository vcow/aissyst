using System.Globalization;
using aissyst.desktop.Wizard;

namespace aissyst.desktop.ViewModels;

internal sealed class WizardOptionViewModel(WizardOption option, Action<WizardOptionViewModel> selected) : ViewModel
{
	private bool _isSelected;
	public string Key => option.Key;
	public string Title => option.Title;
	public string Description => option.Description;
	public string NextLabel => option.NextLabel;
	public WizardAction? SecondaryAction => option.SecondaryAction;
	public bool HasDescription => Description.Length > 0;
	public bool IsSelected
	{
		get => _isSelected;
		set
		{
			if (_isSelected == value)
			{
				return;
			}
			_isSelected = value;
			Notify();
			if (value)
			{
				selected(this);
			}
		}
	}
}

internal sealed class WizardViewModel : ViewModel
{
	private string _path = string.Empty;
	private bool _isBrowsing;
	private WizardOptionViewModel? _selected;
	public string Title { get; private set; } = "Setup";
	public string Description { get; private set; } = string.Empty;
	public string Error { get; private set; } = string.Empty;
	public bool HasError => Error.Length > 0;
	public bool IsWaiting { get; private set; } = true;
	public bool IsFailed { get; private set; }
	public bool IsWorking => IsWaiting && !IsFailed;
	public string ProgressText { get; private set; } = "Please wait...";
	public double ProgressValue { get; private set; }
	public bool IsIndeterminate { get; private set; } = true;
	public bool IsTemplatePath { get; private set; }
	public bool IsChoicePage => !IsTemplatePath;
	public bool IsConfirming { get; private set; }
	public bool CanEdit => !IsWaiting && !IsConfirming;
	public string NextLabel => IsConfirming ? "Cancel" : _selected?.NextLabel ?? "Next";
	public bool HasSecondaryAction => _selected?.SecondaryAction != null;
	public string SecondaryLabel => (IsConfirming ? _selected?.SecondaryAction?.ConfirmationLabel
		: _selected?.SecondaryAction?.Label) ?? string.Empty;
	public string Confirmation => _selected?.SecondaryAction?.Confirmation ?? string.Empty;
	public bool CanNext => !IsWaiting && !IsBrowsing && (IsTemplatePath ? !string.IsNullOrWhiteSpace(Path) : _selected != null);
	public IReadOnlyList<WizardOptionViewModel> Options { get; private set; } = [];
	public string Path
	{
		get => _path;
		set
		{
			_path = value;
			Notify();
			Notify(nameof(CanNext));
		}
	}
	public bool IsBrowsing
	{
		get => _isBrowsing;
		set
		{
			_isBrowsing = value;
			Notify();
			Notify(nameof(CanNext));
		}
	}

	public void Show(WizardPage page)
	{
		Title = page.Title;
		Description = page.Description;
		Error = page.Error;
		IsTemplatePath = page.IsTemplatePath;
		IsConfirming = false;
		IsFailed = false;
		ResetProgress();
		Path = page.Path;
		_selected = null;
		Options = page.Options.Select(option => new WizardOptionViewModel(option, Select)).ToArray();
		var initial = Options.FirstOrDefault(option => option.Key == page.SelectedKey);
		if (initial != null)
		{
			initial.IsSelected = true;
		}
		IsWaiting = false;
		NotifyAll();
	}

	private void Select(WizardOptionViewModel selected)
	{
		_selected = selected;
		IsConfirming = false;
		foreach (var option in Options)
		{
			if (!ReferenceEquals(option, selected))
			{
				option.IsSelected = false;
			}
		}
		Notify(nameof(CanNext));
		NotifyActions();
	}

	public bool TrySubmit(out string value, bool secondary = false)
	{
		value = string.Empty;
		if (!CanNext)
		{
			return false;
		}
		if (secondary)
		{
			if (_selected?.SecondaryAction is not { } action)
			{
				return false;
			}
			if (!IsConfirming)
			{
				IsConfirming = true;
				NotifyActions();
				return false;
			}
			value = action.Key;
		}
		else
		{
			if (IsConfirming)
			{
				IsConfirming = false;
				NotifyActions();
				return false;
			}
			value = IsTemplatePath ? Path.Trim() : _selected!.Key;
		}
		Wait();
		return true;
	}

	public void Wait()
	{
		IsWaiting = true;
		ResetProgress();
		Notify(nameof(IsWaiting));
		Notify(nameof(CanNext));
		Notify(nameof(CanEdit));
		Notify(nameof(IsWorking));
	}

	public void ReportProgress(string message, long? completedBytes, long? totalBytes)
	{
		if (!IsWaiting || IsFailed)
		{
			return;
		}
		IsIndeterminate = completedBytes == null || totalBytes is null or <= 0;
		ProgressValue = IsIndeterminate ? 0 : Math.Clamp(completedBytes!.Value * 100d / totalBytes!.Value, 0, 100);
		var caption = message.TrimEnd('.', ' ');
		if (completedBytes.HasValue)
		{
			ProgressText = IsIndeterminate
				? string.Create(CultureInfo.InvariantCulture, $"{caption}: {completedBytes.Value / 1_000_000d:0.0} MB")
				: string.Create(CultureInfo.InvariantCulture,
					$"{caption}: {ProgressValue:0}% — {completedBytes.Value / 1_000_000d:0.0} / {totalBytes!.Value / 1_000_000d:0.0} MB");
		}
		else
		{
			ProgressText = message;
		}
		NotifyProgress();
	}

	private void ResetProgress()
	{
		ProgressText = "Please wait...";
		ProgressValue = 0;
		IsIndeterminate = true;
		NotifyProgress();
	}

	private void NotifyProgress()
	{
		Notify(nameof(ProgressText));
		Notify(nameof(ProgressValue));
		Notify(nameof(IsIndeterminate));
	}

	public void Fail(string error)
	{
		IsFailed = true;
		Wait();
		ShowError(error);
	}

	public void ShowError(string error)
	{
		Error = error;
		Notify(nameof(Error));
		Notify(nameof(HasError));
	}

	private void NotifyAll()
	{
		foreach (var property in new[] { nameof(Title), nameof(Description), nameof(Error), nameof(HasError),
			nameof(IsTemplatePath), nameof(IsChoicePage), nameof(Options), nameof(IsWaiting), nameof(CanNext),
			nameof(IsWorking), nameof(IsFailed) })
		{
			Notify(property);
		}
		NotifyActions();
	}

	private void NotifyActions()
	{
		foreach (var property in new[] { nameof(IsConfirming), nameof(CanEdit), nameof(NextLabel),
			nameof(HasSecondaryAction), nameof(SecondaryLabel), nameof(Confirmation) })
		{
			Notify(property);
		}
	}
}
