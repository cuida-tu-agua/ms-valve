namespace SyWater.Valve.Application.Events;

// "data" of the RabbitMQ events published by device-service. Each service keeps its own copy of the
// contract (no shared library between microservices).

public sealed record DeviceLinkedEvent(Guid DeviceId, string SerialNumber, Guid PlaceId, Guid UserId, DateTime LinkedAt)
{
    public const string EventType = "device.linked";
}

public sealed record DeviceUnlinkedEvent(Guid DeviceId, string SerialNumber, Guid PlaceId, Guid UserId, DateTime UnlinkedAt)
{
    public const string EventType = "device.unlinked";
}

/// <summary>State is "OPEN" or "CLOSED". CommandId = the order the device just obeyed (null for periodic reports).</summary>
public sealed record ValveReportedEvent(Guid DeviceId, Guid PlaceId, string State, Guid? CommandId, DateTime ReportedAt)
{
    public const string EventType = "device.valve.reported";
}
