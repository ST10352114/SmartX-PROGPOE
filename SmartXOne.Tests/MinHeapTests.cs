using SmartXOne.Shared.Collections;

namespace SmartXOne.Tests;

public class MinHeapTests
{
    [Fact]
    public void Pop_ReturnsItemsInAscendingOrder()
    {
        var random = new Random(42);
        var values = Enumerable.Range(0, 200).Select(_ => random.Next(-1000, 1000)).ToList();
        var heap = new MinHeap<int>(initialCapacity: 2); // forces several resizes

        foreach (var value in values)
        {
            heap.Push(value);
        }

        var popped = new List<int>();
        while (heap.TryPop(out var item))
        {
            popped.Add(item);
        }

        Assert.Equal(values.Order(), popped);
        Assert.True(heap.IsEmpty);
    }

    [Fact]
    public void Peek_ReturnsMinimumWithoutRemovingIt()
    {
        var heap = new MinHeap<int>();
        heap.Push(5);
        heap.Push(1);
        heap.Push(3);

        Assert.Equal(1, heap.Peek());
        Assert.Equal(3, heap.Count);
    }

    [Fact]
    public void CustomComparer_IsRespected()
    {
        var heap = new MinHeap<string>(Comparer<string>.Create((a, b) => a.Length.CompareTo(b.Length)));
        heap.Push("three");
        heap.Push("a");
        heap.Push("to");

        Assert.Equal("a", heap.Pop());
        Assert.Equal("to", heap.Pop());
        Assert.Equal("three", heap.Pop());
    }

    [Fact]
    public void EmptyHeap_PopAndPeekThrow_TryPopReturnsFalse()
    {
        var heap = new MinHeap<int>();

        Assert.Throws<InvalidOperationException>(() => heap.Pop());
        Assert.Throws<InvalidOperationException>(() => heap.Peek());
        Assert.False(heap.TryPop(out _));
    }

    [Fact]
    public void ThrowingComparer_IsWrappedWithContext()
    {
        var heap = new MinHeap<int>(Comparer<int>.Create((_, _) => throw new FormatException("boom")));
        heap.Push(1);

        var ex = Assert.Throws<InvalidOperationException>(() => heap.Push(2));
        Assert.IsType<FormatException>(ex.InnerException);
    }

    [Fact]
    public void Constructor_RejectsNonPositiveCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MinHeap<int>(initialCapacity: 0));
    }
}
