using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CCWrapper.Core;

/// <summary>Minimal INotifyPropertyChanged base: <c>public string? X { get; set => Set(ref field, value); }</c>.</summary>
public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
