using SmartXOne.Shared.Contracts;

namespace SmartXOne.Client.Services;

/// <summary>
/// Scoped (one per app session) live state for the Sensor Data Ingestion page,
/// built the same way as <see cref="CommandStreamState"/>. The live refresh and
/// the "auto-generate every 5s" simulation loops live here rather than in the
/// page, so leaving the page and coming back keeps the sensor list, the toggle
/// position and the running simulation instead of restarting from blank.
/// </summary>
public sealed class IngestionState : IDisposable
{
    private readonly SmartXApiClient _api;
    private readonly ILogger<IngestionState> _logger;
    private readonly CancellationTokenSource _cts = new();
    private CancellationTokenSource? _autoSimulateCts;
    private bool _started;

    public List<SensorSummary> Sensors { get; private set; } = [];

    public int OpenIssueCount { get; private set; }

    /// <summary>Set when the last refresh failed; the page keeps showing the previous sensor list.</summary>
    public string? LoadError { get; private set; }

    /// <summary><c>true</c> when the last refresh failed because the gateway couldn't be reached at all.</summary>
    public bool IsOffline { get; private set; }

    public bool HasLoadedOnce { get; private set; }

    public bool AutoSimulate { get; private set; }

    public bool Generating { get; private set; }

    public string? SimulateError { get; private set; }

    /// <summary>Raised after every refresh or simulation round - subscribers re-render from the properties above.</summary>
    public event Action? Changed;

    public IngestionState(SmartXApiClient api, ILogger<IngestionState> logger)
    {
        _api = api;
        _logger = logger;
    }

    /// <summary>
    /// Starts the live refresh loop the first time the page is opened. Later
    /// calls do nothing, so returning to the page reuses the running loop.
    /// </summary>
    public Task EnsureStartedAsync()
    {
        if (_started)
        {
            return Task.CompletedTask;
        }

        _started = true;
        _ = LiveRefreshLoopAsync(_cts.Token);
        return RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        try
        {
            // Keep the last good list on failure so the page doesn't go blank.
            Sensors = await _api.GetSensorsAsync(_cts.Token);
            var openIssues = await _api.GetIssuesAsync(status: IssueStatus.Open, ct: _cts.Token);
            OpenIssueCount = openIssues.Count;
            LoadError = null;
            IsOffline = false;
        }
        catch (ApiException ex)
        {
            LoadError = ex.Message;
            IsOffline = ex.IsOffline;
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            return; // disposed mid-refresh - nobody is listening any more
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Refreshing the sensor list failed.");
            LoadError = $"Couldn't refresh sensors: {ex.Message}";
            IsOffline = false;
        }
        finally
        {
            HasLoadedOnce = true;
        }

        RaiseChanged();
    }

    /// <summary>
    /// Keeps the dashboard live without a page reload, independently of the
    /// auto-generate toggle - the standing auto-refresh the engagement feature
    /// needs to actually look alive.
    /// </summary>
    private async Task LiveRefreshLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(4));
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
            _logger.LogError(ex, "Ingestion live refresh loop stopped unexpectedly.");
            LoadError = "Live refresh stopped unexpectedly. Reload the page to restart it.";
            RaiseChanged();
        }
    }

    /// <summary>Posts one simulated reading for every registered sensor, then refreshes.</summary>
    public async Task GenerateOnceAsync()
    {
        if (Generating)
        {
            return; // a manual click and an auto tick must not overlap
        }

        Generating = true;
        SimulateError = null;
        RaiseChanged();
        try
        {
            // Snapshot the list: a live refresh can replace Sensors mid-loop.
            foreach (var sensor in Sensors.ToList())
            {
                var value = MockTelemetryGenerator.NextValue(sensor.Category, sensor.LatestReading);
                var result = await _api.SubmitReadingAsync(sensor.SensorId, value, unit: null, _cts.Token);
                if (!result.Success)
                {
                    SimulateError = $"{sensor.SensorId}: {result.Error}";
                }
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Generating simulated telemetry failed.");
            SimulateError = $"Simulation failed: {ex.Message}";
        }
        finally
        {
            Generating = false;
        }

        await RefreshAsync();
    }

    public void SetAutoSimulate(bool enabled)
    {
        _autoSimulateCts?.Cancel();
        _autoSimulateCts?.Dispose();
        _autoSimulateCts = null;
        AutoSimulate = enabled;

        if (enabled)
        {
            _autoSimulateCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            _ = AutoSimulateLoopAsync(_autoSimulateCts.Token);
        }

        RaiseChanged();
    }

    private async Task AutoSimulateLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (await timer.WaitForNextTickAsync(ct))
            {
                await GenerateOnceAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped via the toggle or service disposal - expected.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto-simulate loop stopped unexpectedly.");
            AutoSimulate = false;
            SimulateError = $"Auto-generate stopped: {ex.Message}";
            RaiseChanged();
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
            // A subscriber failing to re-render must not stop the loops.
            _logger.LogError(ex, "An Ingestion subscriber threw while handling a change.");
        }
    }

    public void Dispose()
    {
        _autoSimulateCts?.Cancel();
        _autoSimulateCts?.Dispose();
        _cts.Cancel();
        _cts.Dispose();
    }
}
