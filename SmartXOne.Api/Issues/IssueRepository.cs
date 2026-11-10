using SmartXOne.Shared.Contracts;

namespace SmartXOne.Api.Issues;

/// <summary>
/// In-memory store for user-reported dashboard issues (no database, same
/// pattern as <see cref="Sensors.SensorRepository"/>: a lock-guarded
/// dictionary, registered as a singleton so the whole app shares one list for
/// its lifetime).
/// </summary>
public sealed class IssueRepository(ILogger<IssueRepository> logger)
{
    private readonly Dictionary<string, IssueRecord> _issues = [];
    private readonly Lock _gate = new();

    /// <summary>Stores a new issue in the <see cref="IssueStatus.Open"/> state.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    /// <exception cref="ArgumentException">The request has no description.</exception>
    public IssueDto Create(CreateIssueRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Description);

        try
        {
            var record = new IssueRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                SensorId = string.IsNullOrWhiteSpace(request.SensorId) ? null : request.SensorId,
                Description = request.Description,
                Severity = request.Severity,
                Status = IssueStatus.Open,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };

            lock (_gate)
            {
                // Add (not the indexer) so a GUID collision surfaces instead of
                // silently overwriting an existing issue.
                _issues.Add(record.Id, record);
            }

            return record.ToDto();
        }
        catch (ArgumentException ex)
        {
            logger.LogError(ex, "Could not store a new issue for sensor {SensorId}: duplicate issue id.", request.SensorId);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected failure creating an issue for sensor {SensorId}.", request.SensorId);
            throw;
        }
    }

    /// <summary>All issues, newest first, optionally filtered by status and/or severity.</summary>
    public IReadOnlyList<IssueDto> GetAll(IssueStatus? status, IssueSeverity? severity)
    {
        try
        {
            lock (_gate)
            {
                return _issues.Values
                    .Where(i => status is null || i.Status == status)
                    .Where(i => severity is null || i.Severity == severity)
                    .OrderByDescending(i => i.CreatedAt)
                    .Select(i => i.ToDto())
                    .ToList();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to list issues (status {Status}, severity {Severity}).", status, severity);
            throw;
        }
    }

    /// <returns><c>null</c> if no issue is registered with <paramref name="id"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="id"/> is null or blank.</exception>
    public IssueDto? SetStatus(string id, IssueStatus status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        try
        {
            lock (_gate)
            {
                if (!_issues.TryGetValue(id, out var record))
                {
                    return null;
                }

                record.Status = status;
                record.UpdatedAt = DateTimeOffset.UtcNow;
                return record.ToDto();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to set issue {IssueId} to {Status}.", id, status);
            throw;
        }
    }

    private sealed class IssueRecord
    {
        public required string Id { get; init; }

        public string? SensorId { get; init; }

        public required string Description { get; init; }

        public required IssueSeverity Severity { get; init; }

        public required IssueStatus Status { get; set; }

        public required DateTimeOffset CreatedAt { get; init; }

        public required DateTimeOffset UpdatedAt { get; set; }

        public IssueDto ToDto() => new(Id, SensorId, Description, Severity, Status, CreatedAt, UpdatedAt);
    }
}
