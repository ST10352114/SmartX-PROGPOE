using SmartXOne.Shared.Contracts;

namespace SmartXOne.Api.Realtime;

/// <summary>
/// (Part 2 requirement - STACK) LIFO history of every manual override
/// command issued through the command stream, so "undo" always means "undo
/// the most recent one" - exactly what a <see cref="Stack{T}"/> gives for
/// free. Guarded by a lock since <see cref="Stack{T}"/> isn't thread-safe.
/// </summary>
public sealed class CommandHistory
{
    private readonly Stack<DeviceCommandDto> _stack = new();
    private readonly Lock _gate = new();

    public void Push(DeviceCommandDto command)
    {
        lock (_gate)
        {
            _stack.Push(command);
        }
    }

    /// <returns>The command that was undone, or <c>null</c> if there was nothing to undo.</returns>
    public DeviceCommandDto? Pop()
    {
        lock (_gate)
        {
            return _stack.Count > 0 ? _stack.Pop() : null;
        }
    }

    public int Count
    {
        get { lock (_gate) { return _stack.Count; } }
    }

    /// <summary>Most recent first - <see cref="Stack{T}"/>'s own enumeration order, unchanged.</summary>
    public IReadOnlyList<DeviceCommandDto> Recent(int count)
    {
        lock (_gate)
        {
            return _stack.Take(count).ToList();
        }
    }
}
