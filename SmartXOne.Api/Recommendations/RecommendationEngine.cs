using SmartXOne.Api.Realtime;
using SmartXOne.Shared.Contracts;

namespace SmartXOne.Api.Recommendations;

/// <summary>
/// (Part 2 requirement - Predictive Action &amp; Recommendation Engine)
/// Learns which searches/commands tend to follow a device condition (going
/// Critical, or disconnecting), and surfaces that alongside fixed rule
/// checks as "Suggested Actions".
/// <para>
/// <b>Why <c>Dictionary&lt;string, Dictionary&lt;string, int&gt;&gt;</c>:</b>
/// this is a first-order Markov transition count - for every "from" event key
/// (a condition, e.g. <c>"ConditionCritical:ENV-B"</c>) it maps each "to" key
/// that has followed it within <see cref="CorrelationWindow"/> to how many
/// times that happened. That's exactly an adjacency map with weighted edges,
/// and a nested <see cref="Dictionary{TKey,TValue}"/> gives O(1) average
/// lookup both ways: "what follows this condition?" is one dictionary lookup
/// plus an `OrderByDescending` over a handful of edges, never a scan of the
/// whole event history. The outer and inner dictionaries only ever grow to
/// the number of distinct event keys actually seen (bounded by the event log
/// itself, see <see cref="MaxEvents"/>), so it stays small for a PoE-scale demo.
/// </para>
/// <para>
/// Building the transition graph is O(n) per new event (every existing event
/// in the bounded log is checked once as a possible "from"), which is fine
/// at <see cref="MaxEvents"/> = 500; a production-scale version would index
/// condition events separately instead of scanning the whole log.
/// </para>
/// </summary>
public sealed class RecommendationEngine
{
    private const int MaxEvents = 500;
    private const int MaxSuggestions = 10;
    private static readonly TimeSpan CorrelationWindow = TimeSpan.FromMinutes(5);

    private readonly DeviceRegistry _registry;
    private readonly List<RecommendationEvent> _events = [];
    private readonly Dictionary<string, Dictionary<string, int>> _transitions = [];
    private readonly Lock _gate = new();

    public RecommendationEngine(DeviceRegistry registry)
    {
        _registry = registry;
    }

    public void LogSearch(string term)
    {
        if (!string.IsNullOrWhiteSpace(term))
        {
            LogEvent(RecommendationEventType.Search, term.Trim().ToUpperInvariant());
        }
    }

    internal void LogCommand(string? deviceId, DeviceCommandType type)
        => LogEvent(RecommendationEventType.CommandIssued, deviceId is null ? type.ToString() : $"{deviceId}:{type}");

    internal void LogConditionCritical(string deviceId) => LogEvent(RecommendationEventType.ConditionCritical, deviceId);

    internal void LogConditionDisconnected(string deviceId) => LogEvent(RecommendationEventType.ConditionDisconnected, deviceId);

    /// <summary>
    /// Records <paramref name="type"/>/<paramref name="subject"/>, and for every
    /// condition event already in the log within <see cref="CorrelationWindow"/>
    /// before it, increments the transition count from that condition to this
    /// new event - "this followed that, within the window, one more time".
    /// </summary>
    private void LogEvent(RecommendationEventType type, string subject)
    {
        var newEvent = new RecommendationEvent(type, subject, DateTimeOffset.UtcNow);

        lock (_gate)
        {
            foreach (var priorEvent in _events)
            {
                var isCondition = priorEvent.Type is RecommendationEventType.ConditionCritical or RecommendationEventType.ConditionDisconnected;
                if (!isCondition)
                {
                    continue; // only conditions are "triggers" we learn responses to
                }

                if (newEvent.Timestamp - priorEvent.Timestamp > CorrelationWindow)
                {
                    continue;
                }

                if (priorEvent.Key == newEvent.Key)
                {
                    continue; // don't link a condition to itself
                }

                if (!_transitions.TryGetValue(priorEvent.Key, out var edges))
                {
                    edges = [];
                    _transitions[priorEvent.Key] = edges;
                }

                edges[newEvent.Key] = edges.GetValueOrDefault(newEvent.Key) + 1;
            }

            _events.Add(newEvent);
            if (_events.Count > MaxEvents)
            {
                _events.RemoveAt(0);
            }
        }
    }

    /// <summary>
    /// Rule-based suggestions (every device currently Critical or
    /// disconnected) plus learned suggestions (the most-observed follow-on
    /// for each of those same active conditions), ranked by confidence.
    /// </summary>
    public List<SuggestionDto> GetSuggestions()
    {
        var suggestions = new List<SuggestionDto>();

        lock (_gate)
        {
            foreach (var device in _registry.GetAll())
            {
                if (device.LastSeverity == AlertSeverity.Critical)
                {
                    suggestions.Add(new SuggestionDto(
                        SuggestionKind.Rule,
                        $"{device.Id} is Critical",
                        $"Latest reading {device.LastValue:0.#}{device.Unit} is outside the safe range - review it.",
                        ExecutableDeviceId: null,
                        ExecutableCommand: null,
                        Confidence: 100));

                    AddLearnedSuggestion(suggestions, device.Id, RecommendationEventType.ConditionCritical);
                }

                if (device.IsDisconnected)
                {
                    suggestions.Add(new SuggestionDto(
                        SuggestionKind.Rule,
                        $"{device.Id} is disconnected",
                        "No telemetry received recently - check power/connectivity for this node.",
                        ExecutableDeviceId: null,
                        ExecutableCommand: null,
                        Confidence: 100));

                    AddLearnedSuggestion(suggestions, device.Id, RecommendationEventType.ConditionDisconnected);
                }
            }
        }

        return suggestions
            .OrderByDescending(s => s.Confidence)
            .Take(MaxSuggestions)
            .ToList();
    }

    private void AddLearnedSuggestion(List<SuggestionDto> suggestions, string deviceId, RecommendationEventType conditionType)
    {
        var fromKey = new RecommendationEvent(conditionType, deviceId, DateTimeOffset.UtcNow).Key;
        if (!_transitions.TryGetValue(fromKey, out var edges) || edges.Count == 0)
        {
            return;
        }

        var top = edges.OrderByDescending(e => e.Value).First();
        var (execDeviceId, execCommand) = TryParseCommandKey(top.Key);

        suggestions.Add(new SuggestionDto(
            SuggestionKind.Learned,
            $"Suggested follow-up for {deviceId}",
            $"When this happened before, it was usually followed by {Describe(top.Key)} ({top.Value}x observed).",
            execDeviceId,
            execCommand,
            top.Value));
    }

    private static (string? DeviceId, DeviceCommandType? Command) TryParseCommandKey(string key)
    {
        const string prefix = nameof(RecommendationEventType.CommandIssued) + ":";
        if (!key.StartsWith(prefix, StringComparison.Ordinal))
        {
            return (null, null);
        }

        var parts = key[prefix.Length..].Split(':');
        return parts.Length == 2 && Enum.TryParse<DeviceCommandType>(parts[1], out var command)
            ? (parts[0], command)
            : (null, null);
    }

    private static string Describe(string key)
    {
        var separator = key.IndexOf(':');
        var type = key[..separator];
        var subject = key[(separator + 1)..];

        return type switch
        {
            nameof(RecommendationEventType.Search) => $"a search for \"{subject}\"",
            nameof(RecommendationEventType.CommandIssued) => $"the command {subject}",
            _ => subject,
        };
    }
}
