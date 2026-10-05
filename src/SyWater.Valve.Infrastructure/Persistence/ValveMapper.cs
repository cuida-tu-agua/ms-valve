using SyWater.Valve.Domain.Valves;
using SyWater.Valve.Infrastructure.Persistence.Entities;

namespace SyWater.Valve.Infrastructure.Persistence;

/// <summary>Domain ⇄ table rows. Enums are stored as the SAME strings the CHECK constraints allow.</summary>
internal static class ValveMapper
{
    public static PlaceValve ToDomain(ValveStateEntity e) =>
        PlaceValve.Restore(e.PlaceId, e.DeviceId, StateFromDb(e.State), Utc(e.LastConfirmedAt));

    public static ValveCommand ToDomain(ValveCommandEntity e) => ValveCommand.Restore(
        e.Id, e.PlaceId, e.DeviceId,
        e.Action == "CLOSE" ? ValveAction.Close : ValveAction.Open,
        e.Origin == "AUTO_LEAK" ? CommandOrigin.AutoLeak : CommandOrigin.Manual,
        StatusFromDb(e.Status), e.RequestedBy, e.RequestedByName, e.ConfirmedWithCode, Utc(e.CreatedAt), Utc(e.SentAt),
        Utc(e.TimeoutAt), Utc(e.AcknowledgedAt), e.FailureReason);

    public static ValveCommandEntity ToEntity(ValveCommand c) => new()
    {
        Id = c.Id,
        PlaceId = c.PlaceId,
        DeviceId = c.DeviceId,
        Action = c.Action == ValveAction.Close ? "CLOSE" : "OPEN",
        Origin = c.Origin == CommandOrigin.AutoLeak ? "AUTO_LEAK" : "MANUAL",
        Status = ToDb(c.Status),
        RequestedBy = c.RequestedBy,
        RequestedByName = c.RequestedByName,
        ConfirmedWithCode = c.ConfirmedWithCode,
        CreatedAt = c.CreatedAt,
        SentAt = c.SentAt,
        TimeoutAt = c.TimeoutAt,
        AcknowledgedAt = c.AcknowledgedAt,
        FailureReason = c.FailureReason,
    };

    public static string ToDb(ValveState s) => s switch
    {
        ValveState.Open => "OPEN",
        ValveState.Closed => "CLOSED",
        _ => "UNKNOWN",
    };

    public static string ToDb(CommandStatus s) => s switch
    {
        CommandStatus.Sent => "SENT",
        CommandStatus.AckSuccess => "ACK_SUCCESS",
        CommandStatus.AckTimeout => "ACK_TIMEOUT",
        _ => "FAILED",
    };

    private static ValveState StateFromDb(string s) => s switch
    {
        "OPEN" => ValveState.Open,
        "CLOSED" => ValveState.Closed,
        _ => ValveState.Unknown,
    };

    private static CommandStatus StatusFromDb(string s) => s switch
    {
        "SENT" => CommandStatus.Sent,
        "ACK_SUCCESS" => CommandStatus.AckSuccess,
        "ACK_TIMEOUT" => CommandStatus.AckTimeout,
        _ => CommandStatus.Failed,
    };

    // SQL Server returns DateTime without Kind: they are UTC (we only store UTC)
    private static DateTime Utc(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc);
    private static DateTime? Utc(DateTime? d) => d is null ? null : Utc(d.Value);
}
