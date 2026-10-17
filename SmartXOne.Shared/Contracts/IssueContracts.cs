namespace SmartXOne.Shared.Contracts;

/// <summary>How urgent a user-reported issue is. Drives the dashboard's severity filter and colour coding.</summary>
public enum IssueSeverity
{
    Low,
    Medium,
    High,
    Critical,
}

/// <summary>An issue's place in its own lifecycle.</summary>
public enum IssueStatus
{
    Open,
    Acknowledged,
    Resolved,
}

/// <param name="SensorId">Optional - the sensor this issue is about, if any.</param>
/// <param name="Description">What's wrong, in the reporter's own words.</param>
/// <param name="Severity">How urgent it is.</param>
public record CreateIssueRequest(string? SensorId, string Description, IssueSeverity Severity);

public record IssueDto(
    string Id,
    string? SensorId,
    string Description,
    IssueSeverity Severity,
    IssueStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
