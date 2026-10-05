namespace SyWater.Valve.Domain.Valves;

/// <summary>
/// The valve of ONE place (1 place → 1 device → 1 valve). Created when a device is linked,
/// removed when it is unlinked. Its state only changes when the DEVICE reports (HU-019).
/// </summary>
public sealed class PlaceValve
{
    public Guid PlaceId { get; }
    public Guid DeviceId { get; }
    public ValveState State { get; private set; }
    public DateTime? LastConfirmedAt { get; private set; }

    private PlaceValve(Guid placeId, Guid deviceId, ValveState state, DateTime? lastConfirmedAt)
    {
        PlaceId = placeId;
        DeviceId = deviceId;
        State = state;
        LastConfirmedAt = lastConfirmedAt;
    }

    /// <summary>A device was just linked: nothing confirmed yet.</summary>
    public static PlaceValve ForNewDevice(Guid placeId, Guid deviceId) => new(placeId, deviceId, ValveState.Unknown, null);

    public static PlaceValve Restore(Guid placeId, Guid deviceId, ValveState state, DateTime? lastConfirmedAt) =>
        new(placeId, deviceId, state, lastConfirmedAt);

    /// <summary>
    /// The device reported. An OLDER report than the one we have (RabbitMQ does not promise order)
    /// is ignored. Returns true if something changed.
    /// </summary>
    public bool ApplyReport(ValveState reported, DateTime reportedAt)
    {
        if (reported == ValveState.Unknown) throw new ArgumentException("A device reports OPEN or CLOSED.", nameof(reported));
        if (LastConfirmedAt is { } last && reportedAt < last) return false;

        State = reported;
        LastConfirmedAt = reportedAt;
        return true;
    }

    /// <summary>HU-019 "Sin comunicación": never confirmed, or not for a while.</summary>
    public ValveCommunication CommunicationAt(DateTime now, ValvePolicy policy) =>
        LastConfirmedAt is { } last && now - last <= policy.CommunicationWindow
            ? ValveCommunication.Ok
            : ValveCommunication.NoCommunication;
}
