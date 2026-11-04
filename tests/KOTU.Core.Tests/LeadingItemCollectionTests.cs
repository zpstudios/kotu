using System.Collections.ObjectModel;
using System.Collections.Specialized;
using KOTU.Core.Collections;
using Xunit;

namespace KOTU.Core.Tests;

public class LeadingItemCollectionTests
{
    [Fact]
    public void Source_changes_preserve_leading_identity_and_source_order()
    {
        var leading = new object();
        var a = new object(); var b = new object(); var c = new object();
        var source = new ObservableCollection<object> { a, b };
        var view = new LeadingItemCollection<object>(leading, source);
        void Check()
        {
            Assert.Same(leading, view[0]);
            Assert.Equal(source, view.Skip(1));
            Assert.Equal(1, view.Count(x => ReferenceEquals(x, leading)));
        }
        Check();
        source.Insert(0, c); Check();
        source.Move(0, 2); Check();
        source[1] = new object(); Check();
        source.RemoveAt(0); Check();
        source.Clear(); Check();
        Assert.Single(view);
        source.Add(a); Check();
    }

    [Fact]
    public void Incremental_changes_shift_indices_without_resetting_unchanged_items()
    {
        var source = new ObservableCollection<string> { "alpha", "beta" };
        var view = new LeadingItemCollection<string>(new object(), source);
        var events = new List<NotifyCollectionChangedEventArgs>();
        ((INotifyCollectionChanged)view).CollectionChanged += (_, e) => events.Add(e);
        source.Insert(1, "amber");
        Assert.Equal(NotifyCollectionChangedAction.Add, events[^1].Action);
        Assert.Equal(2, events[^1].NewStartingIndex);
        source.Move(2, 0);
        Assert.Equal(NotifyCollectionChangedAction.Move, events[^1].Action);
        Assert.Equal(3, events[^1].OldStartingIndex);
        Assert.Equal(1, events[^1].NewStartingIndex);
        source[1] = "changed";
        Assert.Equal(NotifyCollectionChangedAction.Replace, events[^1].Action);
        Assert.Equal(2, events[^1].NewStartingIndex);
        source.RemoveAt(0);
        Assert.Equal(NotifyCollectionChangedAction.Remove, events[^1].Action);
        Assert.Equal(1, events[^1].OldStartingIndex);
        Assert.DoesNotContain(events, e => e.Action == NotifyCollectionChangedAction.Reset);
    }
}
