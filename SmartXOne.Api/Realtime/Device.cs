using SmartXOne.Shared.Contracts;

namespace SmartXOne.Api.Realtime;

/// <summary>Live, mutable state for one Part-2 device - what <see cref="DeviceRegistry"/> keys by id.</summary>
internal sealed class Device
{
    public required string Id { get; init; }

    public required RealtimeDeviceKind Kind { get; init; }

    public double LastValue { get; set; }

    public string Unit { get; set; } = "";

    public DateTimeOffset LastSeen { get; set; }

    public AlertSeverity LastSeverity { get; set; } = AlertSeverity.Normal;

    public bool PumpOn { get; set; }

    public bool RelayOn { get; set; }

    public bool ValveOpen { get; set; } = true;

    public DeviceStateDto State => new(PumpOn, RelayOn, ValveOpen);

    public void ApplyState(DeviceStateDto state)
    {
        PumpOn = state.PumpOn;
        RelayOn = state.RelayOn;
        ValveOpen = state.ValveOpen;
    }

    public DeviceDto ToDto(bool isDisconnected) => new(Id, Kind, LastValue, Unit, LastSeen, LastSeverity, isDisconnected, State);
}
