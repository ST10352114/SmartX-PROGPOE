using SmartXOne.Shared.Contracts;

namespace SmartXOne.Client.Services;

/// <summary>
/// Scoped (one instance per app session, not per component) live state for the
/// Real-Time Command Stream page. The polling loop lives here, not in
/// <c>CommandStream.razor</c>'s own fields, so navigating away from the page
/// and back doesn't lose anything or restart from a blank screen - the
/// service keeps polling in the background the whole time the app is open,
/// and the page just subscribes to <see cref="Changed"/> to redraw whenever
/// new data arrives.
/// </summary>
public sealed class CommandStreamState : IDisposable
{
    private readonly SmartXApiClient _api;
    private readonly CancellationTokenSource _cts = new();

    public List<DeviceDto> Devices { get; private set; } = [];

    public List<DeviceCommandDto> RecentCommands { get; private set; } = [];

    public QueueStatusDto? QueueStatus { get; private set; }

    /// <summary>Set when the gateway couldn't be reached; the page shows this instead of crashing or going blank, and polling keeps retrying.</summary>
    public string? LoadError { get; private set; }

    public bool HasLoadedOnce { get; private set; }

    /// <summary>Raised after every refresh (success or failure) - subscribers re-render from the properties above.</summary>
    public event Action? Changed;

    public CommandStreamState(SmartXApiClient api)
    {
        _api = api;
        _ = PollLoopAsync(_cts.Token);
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        await RefreshAsync(); // first load happens immediately, not after the first tick

        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                await RefreshAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // App shutting down / service disposed - expected.
        }
    }

    public async Task RefreshAsync()
    {
        try
        {
            Devices = await _api.GetRealtimeDevicesAsync();
            RecentCommands = await _api.GetRecentCommandsAsync();
            QueueStatus = await _api.GetQueueStatusAsync();
            LoadError = null;
        }
        catch (Exception ex)
        {
            // Deliberately caught here, not left to bubble into a page's
            // render and crash it: the gateway being briefly unreachable is
            // an expected condition (dev API not started yet, restarted,
            // network hiccup), and the polling loop will just try again on
            // the next tick.
            LoadError = ex.Message;
        }
        finally
        {
            HasLoadedOnce = true;
            Changed?.Invoke();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
