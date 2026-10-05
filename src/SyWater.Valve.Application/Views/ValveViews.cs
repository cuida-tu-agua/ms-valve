using SyWater.Valve.Domain.Valves;

namespace SyWater.Valve.Application.Views;

/// <summary>HU-019: what the panel shows.</summary>
public sealed record ValveView(
    Guid PlaceId,
    ValveState State,
    ValveCommunication Communication,
    DateTime? LastConfirmedAt,
    ValveCommandView? PendingCommand);

/// <summary>One order (HU-020/021 progress, HU-022 history line).</summary>
public sealed record ValveCommandView(
    Guid Id,
    ValveAction Action,
    CommandStatus Status,
    CommandOrigin Origin,
    Guid? RequestedBy,
    string? RequestedByName,
    DateTime CreatedAt,
    DateTime? SentAt,
    DateTime TimeoutAt,
    DateTime? AcknowledgedAt,
    string? FailureReason)
{
    public static ValveCommandView From(ValveCommand c) => new(c.Id, c.Action, c.Status, c.Origin, c.RequestedBy,
        c.RequestedByName, c.CreatedAt, c.SentAt, c.TimeoutAt, c.AcknowledgedAt, c.FailureReason);
}

/// <summary>Who is asking (from the token).</summary>
public sealed record Requester(Guid UserId, string? Name);
