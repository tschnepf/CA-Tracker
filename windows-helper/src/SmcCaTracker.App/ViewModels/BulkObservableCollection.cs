using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace SmcCaTracker.App.ViewModels;

/// <summary>
/// An ObservableCollection whose contents can be swapped in one step. Clearing and adding rows one by
/// one makes a grouped, sorted grid re-group after every row; <see cref="ReplaceAll"/> raises a single
/// reset instead, so the list redraws once.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
