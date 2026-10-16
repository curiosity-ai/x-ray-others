using System.Collections.ObjectModel;

namespace XRay.Ics;

/// <summary>A list of typed components that keeps the owning calendar's raw tree in step.</summary>
public sealed class ComponentList<T> : Collection<T>
    where T : CalendarComponent
{
    private readonly Calendar owner;

    internal ComponentList(Calendar owner)
    {
        this.owner = owner;
    }

    internal void AddLoaded(T item)
    {
        base.InsertItem(Count, item);
        item.Calendar = owner;
    }

    protected override void InsertItem(int index, T item)
    {
        ArgumentNullException.ThrowIfNull(item);
        base.InsertItem(index, item);
        item.Calendar = owner;
        owner.Raw.Children.Add(item.Raw);
    }

    protected override void RemoveItem(int index)
    {
        var item = this[index];
        base.RemoveItem(index);
        item.Calendar = null;
        owner.Raw.Children.Remove(item.Raw);
    }

    protected override void SetItem(int index, T item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var old = this[index];
        base.SetItem(index, item);
        old.Calendar = null;
        item.Calendar = owner;
        var position = owner.Raw.Children.IndexOf(old.Raw);
        if (position >= 0)
        {
            owner.Raw.Children[position] = item.Raw;
        }
        else
        {
            owner.Raw.Children.Add(item.Raw);
        }
    }

    protected override void ClearItems()
    {
        foreach (var item in this)
        {
            item.Calendar = null;
            owner.Raw.Children.Remove(item.Raw);
        }

        base.ClearItems();
    }
}
