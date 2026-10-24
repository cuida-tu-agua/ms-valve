using SyWater.Valve.Domain.Valves;

namespace SyWater.Valve.Application.Ports.Out;

/// <summary>Persistence of the valve of each place.</summary>
public interface IValveRepository
{
    Task<PlaceValve?> GetByPlaceAsync(Guid placeId, CancellationToken ct);
    Task<PlaceValve?> GetByDeviceAsync(Guid deviceId, CancellationToken ct);

    /// <summary>A device was linked: replaces any old row of that place or that device with a fresh UNKNOWN one.</summary>
    Task ReplaceAsync(PlaceValve valve, DateTime now, CancellationToken ct);

    /// <summary>The device was unlinked: removes the valve of that place (only if it is still that device).</summary>
    Task RemoveAsync(Guid placeId, Guid deviceId, CancellationToken ct);

    /// <summary>Saves state + last_confirmed_at, only if this report is newer than the stored one.</summary>
    Task SaveReportAsync(PlaceValve valve, DateTime now, CancellationToken ct);
}

/// <summary>Persistence of the orders (never deleted: HU-022 history).</summary>
public interface IValveCommandRepository
{
    /// <summary>Throws CommandInProgressException if the place already has a SENT order (UX_vcmd_place_pending).</summary>
    Task AddAsync(ValveCommand command, CancellationToken ct);

    Task<ValveCommand?> GetAsync(Guid placeId, Guid commandId, CancellationToken ct);
    Task<ValveCommand?> GetByIdAsync(Guid commandId, CancellationToken ct);
    Task<ValveCommand?> GetPendingAsync(Guid placeId, CancellationToken ct);

    /// <summary>
    /// Saves the new status, ONLY if the stored one is still SENT. Two endings arriving at the same time
    /// (the device's confirmation and the 30 s timeout) cannot overwrite each other. True if it was saved.
    /// </summary>
    Task<bool> SaveOutcomeAsync(ValveCommand command, CancellationToken ct);

    Task SaveSentAtAsync(ValveCommand command, CancellationToken ct);

    /// <summary>HU-022: orders of a place created in [fromUtc, toUtc), newest first, at most <paramref name="max"/>.</summary>
    Task<IReadOnlyList<ValveCommand>> ListAsync(Guid placeId, DateTime fromUtc, DateTime toUtc, int max, CancellationToken ct);

    /// <summary>SENT orders whose timeout already passed (for the sweeper).</summary>
    Task<IReadOnlyList<ValveCommand>> GetExpiredAsync(DateTime now, CancellationToken ct);
}

/// <summary>Asks place-service (with the user's token) what the user may do in the place: owner, collaborator or nothing.</summary>
public interface IPlaceAccessChecker
{
    /// <returns>The access of the user, or null if they have none (the place may not even exist).</returns>
    /// <exception cref="ExternalServiceUnavailableException">place-service did not answer.</exception>
    Task<PlaceAccess?> GetAccessAsync(Guid placeId, CancellationToken ct);
}

/// <summary>Role (OWNER / COLLABORATOR) and permission keys that place-service gives a user in a place.</summary>
public sealed record PlaceAccess(string Role, IReadOnlyCollection<string> Permissions)
{
    public const string ViewDeviceStatus = "VIEW_DEVICE_STATUS";
    public const string ControlValve = "CONTROL_VALVE";

    public bool IsOwner => Role == "OWNER";

    public bool Has(string permission) => IsOwner || Permissions.Contains(permission);
}

public static class PlaceAccessRules
{
    /// <summary>Seeing the valve (state, order progress, history): the owner, or a collaborator who sees the device or controls the valve.</summary>
    public static readonly string[] CanSee = [PlaceAccess.ViewDeviceStatus, PlaceAccess.ControlValve];

    /// <summary>Opening or closing it: the owner, or a collaborator with CONTROL_VALVE.</summary>
    public static readonly string[] CanControl = [PlaceAccess.ControlValve];

    /// <summary>
    /// No access at all is 404, the same as a place that does not exist; access without the permission is 403.
    /// </summary>
    /// <exception cref="PlaceNotFoundException">The user has no access to the place.</exception>
    /// <exception cref="PermissionDeniedException">The user is in the place but has none of the permissions.</exception>
    public static async Task RequireAsync(
        this IPlaceAccessChecker places, Guid placeId, string[] anyOf, CancellationToken ct)
    {
        var access = await places.GetAccessAsync(placeId, ct) ?? throw new PlaceNotFoundException(placeId);
        if (!anyOf.Any(access.Has)) throw new PermissionDeniedException(anyOf[0]);
    }
}

/// <summary>Asks ms-iam (with the user's token) to check and consume the e-mailed code (HU-020).</summary>
public interface IConfirmationCodeVerifier
{
    /// <exception cref="ConfirmationCodeRejectedException">Wrong or expired code.</exception>
    /// <exception cref="ExternalServiceUnavailableException">ms-iam did not answer.</exception>
    Task VerifyAsync(string action, string code, CancellationToken ct);
}

/// <summary>Sends the order to the ESP32 through device-service, the MQTT bridge (architecture: valve → device).</summary>
public interface IDeviceGateway
{
    /// <exception cref="ValveDeviceOfflineException">The device is not connected.</exception>
    /// <exception cref="ValveNotFoundException">The place has no device any more.</exception>
    /// <exception cref="ExternalServiceUnavailableException">device-service or the broker is down: the order did NOT leave.</exception>
    /// <exception cref="DeliveryUncertainException">Timeout or unexpected error: the order MAY have left.</exception>
    Task<DateTime> SendAsync(Guid placeId, Guid commandId, ValveAction action, CancellationToken ct);
}

/// <summary>
/// device-service did not answer in time (or failed after receiving the order): the order MAY have
/// reached the ESP32. The order stays SENT; the device's report or the 30 s timeout decide how it ends.
/// </summary>
public sealed class DeliveryUncertainException(Exception? inner = null)
    : Exception("It is not known whether the order reached the device.", inner);

/// <summary>Another microservice is down or answered with an unexpected error. HTTP 503.</summary>
public sealed class ExternalServiceUnavailableException(string service, Exception? inner = null)
    : Exception($"The service '{service}' is not available right now.", inner)
{
    public string Service { get; } = service;
}
