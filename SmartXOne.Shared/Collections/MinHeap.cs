namespace SmartXOne.Shared.Collections;

// Code Attribution
// Purpose: Array-backed binary min-heap - parent/child index arithmetic, sift-up on insert, sift-down after removing the root
// Author: GeeksforGeeks
// Title: Introduction to Min-Heap
// Year: 2026c
// URL/DOI: https://www.geeksforgeeks.org/dsa/introduction-to-min-heap-data-structure/
// Accessed: 10 November 2026

/// <summary>
/// A binary min-heap stored in a flat array: the children of index <c>i</c>
/// live at <c>2i + 1</c> and <c>2i + 2</c>, and its parent at <c>(i - 1) / 2</c>.
/// The smallest item (by the supplied <see cref="IComparer{T}"/>) is always at
/// index 0. Prim's algorithm uses it to pick the cheapest frontier link next.
/// <para>Not thread-safe: callers that share an instance must lock around it.</para>
/// </summary>
/// <remarks>
/// <see cref="Push"/> and <see cref="Pop"/> are O(log n) (one sift along a
/// root-to-leaf path); <see cref="Peek"/> is O(1). The backing array doubles
/// when full, so pushes are amortised O(log n).
/// </remarks>
public sealed class MinHeap<T>
{
    private readonly IComparer<T> _comparer;
    private T[] _items;

    /// <exception cref="ArgumentOutOfRangeException"><paramref name="initialCapacity"/> is less than 1.</exception>
    public MinHeap(IComparer<T>? comparer = null, int initialCapacity = 16)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(initialCapacity, 1);
        _comparer = comparer ?? Comparer<T>.Default;
        _items = new T[initialCapacity];
    }

    public int Count { get; private set; }

    public bool IsEmpty => Count == 0;

    /// <exception cref="InvalidOperationException">The comparer threw while ordering the item.</exception>
    public void Push(T item)
    {
        if (Count == _items.Length)
        {
            Array.Resize(ref _items, _items.Length * 2);
        }

        _items[Count] = item;
        Count++;
        SiftUp(Count - 1);
    }

    /// <summary>The smallest item, without removing it.</summary>
    /// <exception cref="InvalidOperationException">The heap is empty.</exception>
    public T Peek()
    {
        if (Count == 0)
        {
            throw new InvalidOperationException("The heap is empty.");
        }

        return _items[0];
    }

    /// <summary>Removes and returns the smallest item.</summary>
    /// <exception cref="InvalidOperationException">The heap is empty, or the comparer threw.</exception>
    public T Pop()
    {
        if (!TryPop(out var item))
        {
            throw new InvalidOperationException("The heap is empty.");
        }

        return item;
    }

    /// <summary>Removes the smallest item if there is one.</summary>
    /// <exception cref="InvalidOperationException">The comparer threw.</exception>
    public bool TryPop(out T item)
    {
        if (Count == 0)
        {
            item = default!;
            return false;
        }

        item = _items[0];
        Count--;
        _items[0] = _items[Count];
        _items[Count] = default!; // drop the reference so it can be collected
        if (Count > 0)
        {
            SiftDown(0);
        }

        return true;
    }

    private void SiftUp(int index)
    {
        while (index > 0)
        {
            var parent = (index - 1) / 2;
            if (Compare(_items[index], _items[parent]) >= 0)
            {
                return;
            }

            (_items[index], _items[parent]) = (_items[parent], _items[index]);
            index = parent;
        }
    }

    private void SiftDown(int index)
    {
        while (true)
        {
            var left = (2 * index) + 1;
            var right = left + 1;
            var smallest = index;

            if (left < Count && Compare(_items[left], _items[smallest]) < 0)
            {
                smallest = left;
            }

            if (right < Count && Compare(_items[right], _items[smallest]) < 0)
            {
                smallest = right;
            }

            if (smallest == index)
            {
                return;
            }

            (_items[index], _items[smallest]) = (_items[smallest], _items[index]);
            index = smallest;
        }
    }

    private int Compare(T a, T b)
    {
        try
        {
            return _comparer.Compare(a, b);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("The heap's comparer failed while ordering items.", ex);
        }
    }
}
