namespace SyWater.Valve.Domain.Valves;

/// <summary>HU-019: what the device last CONFIRMED. Unknown until the first report.</summary>
public enum ValveState { Open, Closed, Unknown }

/// <summary>HU-019 "Sin comunicación": no confirmation from the device for a while.</summary>
public enum ValveCommunication { Ok, NoCommunication }

/// <summary>HU-020 close, HU-021 open.</summary>
public enum ValveAction { Open, Close }

/// <summary>Manual (the user) or automatic (E8, later).</summary>
public enum CommandOrigin { Manual, AutoLeak }

/// <summary>
/// Life of an order:  SENT ──device confirms──▶ ACK_SUCCESS
///                      ├──30 s without answer──▶ ACK_TIMEOUT
///                      └──could not be sent / device did something else──▶ FAILED
/// </summary>
public enum CommandStatus { Sent, AckSuccess, AckTimeout, Failed }

public static class ValveActions
{
    /// <summary>The state the valve must report once the action is done.</summary>
    public static ValveState ExpectedState(this ValveAction action) =>
        action == ValveAction.Close ? ValveState.Closed : ValveState.Open;
}
