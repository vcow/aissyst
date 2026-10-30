using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace aissyst.desktop.ViewModels;

internal abstract class ViewModel : INotifyPropertyChanged
{
	public event PropertyChangedEventHandler? PropertyChanged;

	protected void Notify([CallerMemberName] string? name = null) =>
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
