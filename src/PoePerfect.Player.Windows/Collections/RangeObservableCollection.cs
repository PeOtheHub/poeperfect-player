using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace APTV.Collections;

public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    public RangeObservableCollection()
    {
    }

    public RangeObservableCollection(IEnumerable<T> items)
        : base(items)
    {
    }

    public void AddRange(IEnumerable<T> items)
    {
        var materializedItems = items as IList<T> ?? items.ToList();
        if (materializedItems.Count == 0)
        {
            return;
        }

        CheckReentrancy();

        foreach (var item in materializedItems)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
