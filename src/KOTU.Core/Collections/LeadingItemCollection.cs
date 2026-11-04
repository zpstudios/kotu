using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace KOTU.Core.Collections;

/// <summary>A read-only observable view with one fixed leading item and unchanged source ordering.</summary>
public sealed class LeadingItemCollection<T> : ReadOnlyObservableCollection<object> where T : class
{
    private readonly ObservableCollection<object> _view;
    private readonly ObservableCollection<T> _source;
    private readonly object _leading;

    public LeadingItemCollection(object leading, ObservableCollection<T> source)
        : this(new ObservableCollection<object>(), leading, source) { }

    private LeadingItemCollection(ObservableCollection<object> view, object leading, ObservableCollection<T> source)
        : base(view)
    {
        _view = view;
        _source = source;
        _leading = leading;
        ResetView();
        source.CollectionChanged += OnSourceChanged;
    }

    private void ResetView()
    {
        _view.Clear();
        _view.Add(_leading);
        foreach (var item in _source) _view.Add(item);
    }

    private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                for (var i = 0; i < e.NewItems!.Count; i++)
                    _view.Insert(e.NewStartingIndex + i + 1, e.NewItems[i]!);
                break;
            case NotifyCollectionChangedAction.Remove:
                for (var i = 0; i < e.OldItems!.Count; i++) _view.RemoveAt(e.OldStartingIndex + 1);
                break;
            case NotifyCollectionChangedAction.Replace when e.NewItems!.Count == e.OldItems!.Count:
                for (var i = 0; i < e.NewItems.Count; i++) _view[e.NewStartingIndex + i + 1] = e.NewItems[i]!;
                break;
            case NotifyCollectionChangedAction.Move when e.OldItems!.Count == 1:
                _view.Move(e.OldStartingIndex + 1, e.NewStartingIndex + 1);
                break;
            default:
                ResetView();
                break;
        }
    }
}
