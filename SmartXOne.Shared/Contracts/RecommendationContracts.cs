namespace SmartXOne.Shared.Contracts;

/// <summary>A suggestion either came from a fixed rule (always true the moment a condition holds) or was learned from past event sequences.</summary>
public enum SuggestionKind
{
    Rule,
    Learned,
}

/// <param name="Kind">Rule (immediate, no history needed) or Learned (from past co-occurring events).</param>
/// <param name="Title">Short headline.</param>
/// <param name="Detail">One-sentence explanation a user can act on.</param>
/// <param name="ExecutableDeviceId">
/// Non-null when this suggestion maps to a concrete, one-click command - pass
/// it and <paramref name="ExecutableCommand"/> straight to the existing
/// issue-command endpoint.
/// </param>
/// <param name="Confidence">How many times this pattern was observed (Learned), or a fixed value (Rule) - used to rank suggestions.</param>
public record SuggestionDto(
    SuggestionKind Kind,
    string Title,
    string Detail,
    string? ExecutableDeviceId,
    DeviceCommandType? ExecutableCommand,
    int Confidence);

/// <param name="Term">What the user searched/filtered by.</param>
public record LogSearchRequest(string Term);
