using SyWater.Valve.Application.Ports.In;
using SyWater.Valve.Application.Ports.Out;
using SyWater.Valve.Application.Views;
using SyWater.Valve.Domain.Valves;

namespace SyWater.Valve.Application.UseCases;

/// <summary>
/// HU-020 (close) / HU-021 (open). The order of the checks matters:
///   1. the place is mine (ms-places)                    → 404
///   2. it has a device/valve                            → 404 valve.not_found
///   3. no other order in progress                       → 409 valve.command_in_progress
///   4. not already in that state                        → 409 valve.already_in_state
///      close + no news from the device for 3 min        → 409 valve.device_offline (before spending the code)
///   5. close: the e-mailed code (ms-iam consumes it)     → 400 auth.invalid_code / auth.code_expired
///   6. save the order (SENT, 30 s) and send it through ms-devices → 202, or FAILED + 409/503
/// The code is checked AFTER the cheap checks, so a request that would fail anyway does not burn it.
/// The answer is 202: the order is confirmed only when the device reports (the app polls the order).
/// </summary>
public sealed class RequestValveCommandUseCase(
    IPlaceAccessChecker places,
    IValveRepository valves,
    IValveCommandRepository commands,
    IConfirmationCodeVerifier codes,
    IDeviceGateway devices,
    ValvePolicy policy,
    TimeProvider clock) : IRequestValveCommandUseCase
{
    public const string CloseAction = "VALVE_CLOSE";

    public async Task<ValveCommandView> ExecuteAsync(
        Requester requester, Guid placeId, ValveAction action, string? code, CancellationToken ct)
    {
        if (!await places.CanManageAsync(placeId, ct)) throw new PlaceNotFoundException(placeId);
        var valve = await valves.GetByPlaceAsync(placeId, ct) ?? throw new ValveNotFoundException(placeId);

        var now = clock.GetUtcNow().UtcDateTime;
        var pending = await commands.GetPendingAsync(placeId, ct);
        if (pending is not null)
        {
            if (!pending.TimeOutIfExpired(now)) throw new CommandInProgressException(placeId);
            await commands.SaveOutcomeAsync(pending, ct);
        }

        var communication = valve.CommunicationAt(now, policy);
        if (valve.State == action.ExpectedState() && communication == ValveCommunication.Ok)
            throw new ValveAlreadyInStateException(valve.State);

        var closing = action == ValveAction.Close;
        if (closing)
        {
            // The code is single-use: do not spend it on an order that could not reach the device
            if (communication == ValveCommunication.NoCommunication) throw new ValveDeviceOfflineException();
            if (string.IsNullOrWhiteSpace(code)) throw new ConfirmationCodeRequiredException();
            await codes.VerifyAsync(CloseAction, code.Trim(), ct);
        }

        var command = ValveCommand.Manual(placeId, valve.DeviceId, action, requester.UserId, requester.Name, closing, now, policy);
        await commands.AddAsync(command, ct);   // UX_vcmd_place_pending: two phones at once → one gets 409

        try
        {
            command.MarkSent(await devices.SendAsync(placeId, command.Id, action, ct));
            await commands.SaveSentAtAsync(command, ct);
        }
        catch (DeliveryUncertainException)
        {
            // Maybe it arrived: keep it SENT. The device's report (ACK) or the 30 s sweeper will end it.
            return ValveCommandView.From(command);
        }
        catch (Exception ex) when (ex is ValveDeviceOfflineException or ValveNotFoundException or ExternalServiceUnavailableException)
        {
            command.Fail(ex is ValveDeviceOfflineException ? "The device was not connected." : ex.Message);
            await commands.SaveOutcomeAsync(command, ct);
            throw;
        }

        return ValveCommandView.From(command);
    }
}
