using SmartXOne.Shared.Contracts;

namespace SmartXOne.Api.Issues;

/// <summary>
/// In-memory store for user-reported dashboard issues (no database, same
/// pattern as <see cref="Sensors.SensorRepository"/>: a lock-guarded
/// dictionary, registered as a singleton so the whole app shares one list for
/// its lifetime).
/// </summary>
public sealed class IssueRepository
{
    private readonly Dictionary<string, IssueRecord> _issues = [];
    private readonly Lock _gate = new();

    public IssueDto Create(CreateIssueRequest request)
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
            _issues[record.Id] = record;
        }

        return record.ToDto();
    }

    /// <summary>All issues, newest first, optionally filtered by status and/or severity.</summary>
    public IReadOnlyList<IssueDto> GetAll(IssueStatus? status, IssueSeverity? severity)
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

    /// <returns><c>null</c> if no issue is registered with <paramref name="id"/>.</returns>
    public IssueDto? SetStatus(string id, IssueStatus status)
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
