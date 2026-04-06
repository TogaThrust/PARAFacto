using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PARAFactoNative.ViewModels;

public abstract class NotifyBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // Compat: certains VMs utilisent encore OnPropertyChanged(...)
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => Raise(name);
}

