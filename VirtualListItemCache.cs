using System.Windows.Forms;

namespace MultiExplorer;

/// <summary>
/// Keeps a small working set of ListViewItem objects for owner-data list views.
/// The native control owns selection and focus state; managed rows only need to
/// remain alive while they are being requested for painting and hit testing.
/// </summary>
internal sealed class VirtualListItemCache
{
    private readonly int _capacity;
    private readonly Dictionary<int, ListViewItem> _items = [];
    private readonly Queue<int> _insertionOrder = [];

    internal VirtualListItemCache(int capacity = 384)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    internal int Count => _items.Count;

    internal ListViewItem GetOrCreate(
        int index,
        Func<ListViewItem> itemFactory)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentNullException.ThrowIfNull(itemFactory);

        if (_items.TryGetValue(index, out ListViewItem? item))
            return item;

        while (_items.Count >= _capacity
               && _insertionOrder.TryDequeue(out int expiredIndex))
        {
            _items.Remove(expiredIndex);
        }

        item = itemFactory();
        _items.Add(index, item);
        _insertionOrder.Enqueue(index);
        return item;
    }

    internal void Clear()
    {
        _items.Clear();
        _insertionOrder.Clear();
    }
}
