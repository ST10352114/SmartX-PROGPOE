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
    private readonly ILogger<CommandStreamState> _logger;
    private readonly CancellationTokenSource _cts = new();

    public List<DeviceDto> Devices { get; private set; } = [];

    public List<DeviceCommandDto> RecentCommands { get; private set; } = [];

    public QueueStatusDto? QueueStatus { get; private set; }

    public List<SuggestionDto> Suggestions { get; private set; } = [];

    /// <summary>Set when the gateway couldn't be reached; the page shows this instead of crashing or going blank, and polling keeps retrying.</summary>
    public string? LoadError { get; private set; }

    /// <summary><c>true</c> when the last refresh failed because the gateway couldn't be reached at all.</summary>
    public bool IsOffline { get; private set; }

    public bool HasLoadedOnce { get; private set; }

    /// <summary>Raised after every refresh (success or failure) - subscribers re-render from the properties above.</summary>
    public event Action? Changed;

    public CommandStreamState(SmartXApiClient api, ILogger<CommandStreamState> logger)
    {
        _api = api;
        _logger = logger;
        _ = PollLoopAsync(_cts.Token);
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
            await RefreshAsync(); // first load happens immediately, not after the first tick

            while (await timer.WaitForNextTickAsync(ct))
            {
                await RefreshAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // App shutting down / service disposed - expected.
        }
        catch (Exception ex)
        {
            // RefreshAsync handles its own failures, so reaching here means the
            // loop itself broke; record it so the page shows why updates stopped.
            _logger.LogError(ex, "Command stream polling loop stopped unexpectedly.");
            LoadError = "Live updates stopped unexpectedly. Reload the page to restart them.";
            RaiseChanged();
        }
    }

    public async Task RefreshAsync()
    {
        try
        {
            Devices = await _api.GetRealtimeDevicesAsync(_cts.Token);
            RecentCommands = await _api.GetRecentCommandsAsync(_cts.Token);
            QueueStatus = await _api.GetQueueStatusAsync(_cts.Token);
            Suggestions = await _api.GetSuggestionsAsync(_cts.Token);
            LoadError = null;
            IsOffline = false;
        }
        catch (ApiException ex)
        {
            // Deliberately caught here, not left to bubble into a page's
            // render and crash it: the gateway being briefly unreachable is
            // an expected condition (dev API not started yet, restarted,
            // network hiccup), and the polling loop will just try again on
            // the next tick.
            LoadError = ex.Message;
            IsOffline = ex.IsOffline;
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            return; // disposed mid-refresh - nobody is listening any more
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected failure refreshing the command stream.");
            LoadError = $"Couldn't refresh the command stream: {ex.Message}";
        }
        finally
        {
            HasLoadedOnce = true;
        }

        RaiseChanged();
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            // A subscriber failing to re-render must not stop the poll loop.
            _logger.LogError(ex, "A Command Stream subscriber threw while handling a refresh.");
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
