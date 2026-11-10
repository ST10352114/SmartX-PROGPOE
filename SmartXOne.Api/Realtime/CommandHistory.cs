using SmartXOne.Shared.Contracts;

namespace SmartXOne.Api.Realtime;

/// <summary>
/// (Part 2 requirement - STACK) LIFO history of every manual override
/// command issued through the command stream, so "undo" always means "undo
/// the most recent one" - exactly what a <see cref="Stack{T}"/> gives for
/// free. Guarded by a lock since <see cref="Stack{T}"/> isn't thread-safe.
/// </summary>
public sealed class CommandHistory(ILogger<CommandHistory> logger)
{
    private readonly Stack<DeviceCommandDto> _stack = new();
    private readonly Lock _gate = new();

    /// <summary>Records <paramref name="command"/> as the newest entry (the next one undo will pop).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is null.</exception>
    public void Push(DeviceCommandDto command)
    {
        ArgumentNullException.ThrowIfNull(command);

        try
        {
            lock (_gate)
            {
                _stack.Push(command);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to record {CommandType} for {DeviceId} in the command history.",
                command.Type, command.DeviceId ?? "(all devices)");
            throw;
        }
    }

    /// <returns>The command that was undone, or <c>null</c> if there was nothing to undo.</returns>
    public DeviceCommandDto? Pop()
    {
        try
        {
            lock (_gate)
            {
                return _stack.TryPop(out var command) ? command : null;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to pop the most recent command from the history.");
            throw;
        }
    }

    public int Count
    {
        get { lock (_gate) { return _stack.Count; } }
    }

    /// <summary>Most recent first - <see cref="Stack{T}"/>'s own enumeration order, unchanged.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    public IReadOnlyList<DeviceCommandDto> Recent(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        try
        {
            lock (_gate)
            {
                return _stack.Take(count).ToList();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to read the {Count} most recent commands.", count);
            throw;
        }
    }
}
