namespace SyWater.Valve.Domain.Valves;

/// <summary>
/// One order to the valve (HU-020/021) and its audit data (HU-022: who, what, when, how it ended).
/// The id travels to the ESP32 and comes back in its report: that is how a report confirms THIS order.
/// </summary>
public sealed class ValveCommand
{
    public const int MaxReasonLength = 500;

    public Guid Id { get; }
    public Guid PlaceId { get; }
    public Guid DeviceId { get; }
    public ValveAction Action { get; }
    public CommandOrigin Origin { get; }
    public CommandStatus Status { get; private set; }
    public Guid? RequestedBy { get; }
    public string? RequestedByName { get; }
    public bool ConfirmedWithCode { get; }
    public DateTime CreatedAt { get; }
    public DateTime? SentAt { get; private set; }
    public DateTime TimeoutAt { get; }
    public DateTime? AcknowledgedAt { get; private set; }
    public string? FailureReason { get; private set; }

    private ValveCommand(Guid id, Guid placeId, Guid deviceId, ValveAction action, CommandOrigin origin, CommandStatus status,
        Guid? requestedBy, string? requestedByName, bool confirmedWithCode, DateTime createdAt, DateTime? sentAt,
        DateTime timeoutAt, DateTime? acknowledgedAt, string? failureReason)
    {
        Id = id;
        PlaceId = placeId;
        DeviceId = deviceId;
        Action = action;
        Origin = origin;
        Status = status;
        RequestedBy = requestedBy;
        RequestedByName = requestedByName;
        ConfirmedWithCode = confirmedWithCode;
        CreatedAt = createdAt;
        SentAt = sentAt;
        TimeoutAt = timeoutAt;
        AcknowledgedAt = acknowledgedAt;
        FailureReason = failureReason;
    }

    /// <summary>A manual order by the owner. Closing requires the e-mailed code (HU-020); opening does not (HU-021).</summary>
    public static ValveCommand Manual(Guid placeId, Guid deviceId, ValveAction action, Guid userId, string? userName,
        bool confirmedWithCode, DateTime now, ValvePolicy policy)
    {
        if (action == ValveAction.Close && !confirmedWithCode) throw new ConfirmationCodeRequiredException();
        var name = string.IsNullOrWhiteSpace(userName) ? null : userName.Trim()[..Math.Min(userName.Trim().Length, 200)];
        return new ValveCommand(Guid.NewGuid(), placeId, deviceId, action, CommandOrigin.Manual, CommandStatus.Sent,
            userId, name, confirmedWithCode, now, null, now + policy.AckTimeout, null, null);
    }

    public static ValveCommand Restore(Guid id, Guid placeId, Guid deviceId, ValveAction action, CommandOrigin origin,
        CommandStatus status, Guid? requestedBy, string? requestedByName, bool confirmedWithCode, DateTime createdAt,
        DateTime? sentAt, DateTime timeoutAt, DateTime? acknowledgedAt, string? failureReason) =>
        new(id, placeId, deviceId, action, origin, status, requestedBy, requestedByName, confirmedWithCode, createdAt, sentAt,
            timeoutAt, acknowledgedAt, failureReason);

    public bool IsPending => Status == CommandStatus.Sent;

    /// <summary>The order left for the device (ms-devices put it on MQTT).</summary>
    public void MarkSent(DateTime sentAt) => SentAt = sentAt;

    /// <summary>
    /// The device reported its valve with THIS order's id. HU-020: "confirma la ejecución solo cuando el
    /// dispositivo reporta el cambio de estado". A late report (after the timeout) does not revive the order.
    /// Returns true if the status changed.
    /// </summary>
    public bool Acknowledge(ValveState reported, DateTime at)
    {
        if (!IsPending) return false;
        if (reported == Action.ExpectedState())
        {
            Status = CommandStatus.AckSuccess;
            AcknowledgedAt = at;
        }
        else
        {
            Status = CommandStatus.Failed;
            AcknowledgedAt = at;
            FailureReason = $"The device reported the valve {reported.ToString().ToUpperInvariant()}.";
        }
        return true;
    }

    /// <summary>HU-020: 30 s without confirmation → failure, the state does not change.</summary>
    public bool TimeOutIfExpired(DateTime now)
    {
        if (!IsPending || now < TimeoutAt) return false;
        Status = CommandStatus.AckTimeout;
        FailureReason = "The device did not confirm within 30 seconds.";
        return true;
    }

    /// <summary>The order could not be delivered (device offline, broker down, device unlinked…).</summary>
    public void Fail(string reason)
    {
        if (!IsPending) return;
        Status = CommandStatus.Failed;
        FailureReason = reason.Length <= MaxReasonLength ? reason : reason[..MaxReasonLength];
    }
}
