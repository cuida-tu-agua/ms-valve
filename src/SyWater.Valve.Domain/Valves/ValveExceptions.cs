using SyWater.Valve.Domain.Common;

namespace SyWater.Valve.Domain.Valves;

/// <summary>The place does not exist or is not yours (same answer on purpose). HTTP 404.</summary>
public sealed class PlaceNotFoundException(Guid placeId)
    : DomainException("place.not_found", $"Place {placeId} was not found.");

/// <summary>The user is a collaborator of the place but the owner did not give them this permission. HTTP 403.</summary>
public sealed class PermissionDeniedException(string permission)
    : DomainException("permission.denied", $"You do not have the permission {permission} in this place.");

/// <summary>The place has no linked device, so there is no valve to see or move. HTTP 404.</summary>
public sealed class ValveNotFoundException(Guid placeId)
    : DomainException("valve.not_found", $"Place {placeId} has no device with a valve.");

/// <summary>Another order for this place is still waiting for the device. HTTP 409.</summary>
public sealed class CommandInProgressException(Guid placeId)
    : DomainException("valve.command_in_progress", $"Place {placeId} already has an order in progress.");

/// <summary>The device already confirmed that state (e.g. close an already closed valve). HTTP 409.</summary>
public sealed class ValveAlreadyInStateException(ValveState state)
    : DomainException("valve.already_in_state", $"The valve is already {state.ToString().ToUpperInvariant()}.")
{
    public ValveState State { get; } = state;
}

/// <summary>The device is not connected, the order would never arrive. HTTP 409.</summary>
public sealed class ValveDeviceOfflineException()
    : DomainException("valve.device_offline", "The device is not connected, so the order cannot reach it.");

/// <summary>HU-020: closing by hand needs the code e-mailed to the user. HTTP 400.</summary>
public sealed class ConfirmationCodeRequiredException()
    : DomainException("valve.code_required", "Closing the valve needs the confirmation code sent to your e-mail.");

/// <summary>ms-iam rejected the code (wrong / expired). Keeps ms-iam's code so the app shows the same message. HTTP 400.</summary>
public sealed class ConfirmationCodeRejectedException(string code, int? remainingAttempts)
    : DomainException(code, "The confirmation code was rejected.")
{
    public int? RemainingAttempts { get; } = remainingAttempts;
}

/// <summary>No order with that id for that place. HTTP 404.</summary>
public sealed class CommandNotFoundException(Guid commandId)
    : DomainException("valve.command_not_found", $"Order {commandId} was not found.");

/// <summary>A device report with a state that is not OPEN or CLOSED. Not HTTP (goes to the dead-letter queue).</summary>
public sealed class InvalidValveReportException(string? state)
    : DomainException("valve.invalid_report", $"Invalid valve state '{state}'.");
