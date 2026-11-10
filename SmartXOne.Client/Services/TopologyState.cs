using SmartXOne.Shared.Contracts;

namespace SmartXOne.Client.Services;

/// <summary>
/// Scoped (one per app session) live state for the Network Topology page,
/// built the same way as <see cref="CommandStreamState"/> and
/// <see cref="IngestionState"/>: polling and the user's current selections
/// (searched route, reachability start) live here, so leaving the page and
/// coming back shows the same view instead of starting over.
/// <para>
/// Each poll fetches the topology, and only when its <c>Version</c> has moved
/// does it also re-fetch the spanning tree, components and the selected route
/// and reachability - so a node going offline (from this page or the
/// simulator's keyboard) shows up as a rerouted path within one poll.
/// </para>
/// </summary>
public sealed class TopologyState : IDisposable
{
    private readonly SmartXApiClient _api;
    private readonly ILogger<TopologyState> _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private bool _started;

    public TopologyDto? Topology { get; private set; }

    public SpanningTreeDto? SpanningTree { get; private set; }

    public List<MeshComponentDto> Components { get; private set; } = [];

    public string? LoadError { get; private set; }

    public bool IsOffline { get; private set; }

    public bool HasLoadedOnce { get; private set; }

    /// <summary>The MAC whose route is being shown, or <c>null</c>.</summary>
    public string? RouteMac { get; private set; }

    public MeshRouteDto? Route { get; private set; }

    public string? RouteError { get; private set; }

    /// <summary>The MAC or subnet the reachability panel sweeps from; <c>null</c> means the gateway.</summary>
    public string? ReachabilityFrom { get; private set; }

    public ReachabilityDto? Reachability { get; private set; }

    public string? ReachabilityError { get; private set; }

    /// <summary>Raised after every refresh or selection change - subscribers re-render from the properties above.</summary>
    public event Action? Changed;

    public TopologyState(SmartXApiClient api, ILogger<TopologyState> logger)
    {
        _api = api;
        _logger = logger;
    }

    /// <summary>Starts polling the first time the page opens; later calls reuse the running loop.</summary>
    public Task EnsureStartedAsync()
    {
        if (_started)
        {
            return Task.CompletedTask;
        }

        _started = true;
        _ = PollLoopAsync(_cts.Token);
        return RefreshAsync();
    }

    public async Task RefreshAsync() => await RefreshCoreAsync(force: true);

    private async Task RefreshCoreAsync(bool force)
    {
        // The poll loop and a button click can overlap; one refresh at a time.
        if (!await _refreshGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            var topology = await _api.GetTopologyAsync(_cts.Token);
            var changed = force || Topology is null || topology.Version != Topology.Version;
            Topology = topology;

            if (changed)
            {
                SpanningTree = await _api.GetSpanningTreeAsync(_cts.Token);
                Components = await _api.GetMeshComponentsAsync(_cts.Token);
                await LoadRouteAsync();
                await LoadReachabilityAsync();
            }

            LoadError = null;
            IsOffline = false;
        }
        catch (ApiException ex)
        {
            // Keep showing the last mesh; the banner explains it's stale.
            LoadError = ex.Message;
            IsOffline = ex.IsOffline;
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            return; // disposed mid-refresh
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Refreshing the mesh topology failed.");
            LoadError = $"Couldn't refresh the topology: {ex.Message}";
            IsOffline = false;
        }
        finally
        {
            HasLoadedOnce = true;
            _refreshGate.Release();
        }

        RaiseChanged();
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
            while (await timer.WaitForNextTickAsync(ct))
            {
                await RefreshCoreAsync(force: false);
            }
        }
        catch (OperationCanceledException)
        {
            // Service disposed - expected.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Topology polling loop stopped unexpectedly.");
            LoadError = "Live topology updates stopped unexpectedly. Reload the page to restart them.";
            RaiseChanged();
        }
    }

    /// <summary>Shows the route from the gateway to <paramref name="mac"/>; blank clears it.</summary>
    public async Task FindRouteAsync(string? mac)
    {
        RouteMac = string.IsNullOrWhiteSpace(mac) ? null : mac.Trim();
        Route = null;
        RouteError = null;

        try
        {
            await LoadRouteAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Finding the route for {Mac} failed.", RouteMac);
            RouteError = $"Couldn't find the route: {ex.Message}";
        }

        RaiseChanged();
    }

    /// <summary>Sweeps reachability from a node MAC or subnet name; blank means the gateway.</summary>
    public async Task CheckReachabilityAsync(string? from)
    {
        ReachabilityFrom = string.IsNullOrWhiteSpace(from) ? null : from.Trim();
        Reachability = null;
        ReachabilityError = null;

        try
        {
            await LoadReachabilityAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reachability sweep from {From} failed.", ReachabilityFrom);
            ReachabilityError = $"Couldn't check reachability: {ex.Message}";
        }

        RaiseChanged();
    }

    /// <summary>Takes a node offline or brings it back, then refreshes so the reroute shows straight away.</summary>
    public async Task<ApiResult<MeshNodeDto>> SetNodeOnlineAsync(string mac, bool online)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mac);

        var result = await _api.SetMeshNodeStateAsync(mac, new SetMeshNodeStateRequest(online, Battery: null), _cts.Token);
        if (result.Success)
        {
            await RefreshAsync();
        }

        return result;
    }

    /// <summary>Loads <see cref="Route"/> for <see cref="RouteMac"/>. An unknown MAC is a user-facing message, not a failure.</summary>
    private async Task LoadRouteAsync()
    {
        if (RouteMac is null)
        {
            return;
        }

        try
        {
            Route = await _api.GetMeshRouteAsync(RouteMac, _cts.Token);
            RouteError = null;
        }
        catch (ApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            Route = null;
            RouteError = $"No mesh node has MAC '{RouteMac}'.";
        }
    }

    private async Task LoadReachabilityAsync()
    {
        try
        {
            Reachability = await _api.GetReachabilityAsync(ReachabilityFrom, _cts.Token);
            ReachabilityError = null;
        }
        catch (ApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            Reachability = null;
            ReachabilityError = ReachabilityFrom is null
                ? "No gateway is registered yet - start the device simulator to build the mesh."
                : $"'{ReachabilityFrom}' is neither a node MAC nor a subnet name.";
        }
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
            _logger.LogError(ex, "A Topology subscriber threw while handling a change.");
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _refreshGate.Dispose();
    }
}
