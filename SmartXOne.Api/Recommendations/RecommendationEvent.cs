namespace SmartXOne.Api.Recommendations;

/// <summary>What kind of thing happened, for the recommendation engine's event log.</summary>
internal enum RecommendationEventType
{
    /// <summary>A user searched/filtered by a term (usually a device id).</summary>
    Search,

    /// <summary>A manual command was issued (single-device or ShutAllValves).</summary>
    CommandIssued,

    /// <summary>A device's reading just crossed into AlertSeverity.Critical (the "trigger" side of a learned pattern).</summary>
    ConditionCritical,

    /// <summary>A device was just marked disconnected (the other "trigger" side).</summary>
    ConditionDisconnected,
}

/// <summary>One entry in the recommendation engine's event log.</summary>
internal sealed record RecommendationEvent(RecommendationEventType Type, string Subject, DateTimeOffset Timestamp)
{
    /// <summary>The dictionary key this event is stored/looked-up under: "{Type}:{Subject}".</summary>
    public string Key => $"{Type}:{Subject}";
}
