using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace LocalDocSearch.Desktop.ViewModels;

/// <summary>One native collection reset for a final reorder, rather than thousands of UI events.</summary>
internal sealed class ResettableObservableCollection<T> : ObservableCollection<T>
{
    internal void ReplaceAll(IReadOnlyList<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
