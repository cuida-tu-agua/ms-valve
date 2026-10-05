using SyWater.Valve.Application.Events;
using SyWater.Valve.Application.Ports.In;
using SyWater.Valve.Application.Ports.Out;
using SyWater.Valve.Domain.Valves;

namespace SyWater.Valve.Application.UseCases;

/// <summary>
/// Keeps the valves in sync with device-service. Every handler is idempotent: RabbitMQ may deliver
/// an event twice or out of order, and the result must be the same.
/// </summary>
public sealed class DeviceEventsHandler(IValveRepository valves, IValveCommandRepository commands, TimeProvider clock)
    : IDeviceEventsHandler
{
    /// <summary>HU-012: the place now has a valve, state UNKNOWN until the device reports.</summary>
    public Task HandleAsync(DeviceLinkedEvent e, CancellationToken ct) =>
        valves.ReplaceAsync(PlaceValve.ForNewDevice(e.PlaceId, e.DeviceId), clock.GetUtcNow().UtcDateTime, ct);

    /// <summary>HU-014: the valve of the place disappears; an order in progress can no longer be confirmed.</summary>
    public async Task HandleAsync(DeviceUnlinkedEvent e, CancellationToken ct)
    {
        var pending = await commands.GetPendingAsync(e.PlaceId, ct);
        if (pending is not null && pending.DeviceId == e.DeviceId)
        {
            pending.Fail("The device was unlinked from the place.");
            await commands.SaveOutcomeAsync(pending, ct);
        }
        await valves.RemoveAsync(e.PlaceId, e.DeviceId, ct);
    }

    /// <summary>HU-019..021: the real state, and the confirmation of the order it carries (if any).</summary>
    public async Task HandleAsync(ValveReportedEvent e, CancellationToken ct)
    {
        var state = (e.State ?? "").Trim().ToUpperInvariant() switch
        {
            "OPEN" => ValveState.Open,
            "CLOSED" => ValveState.Closed,
            _ => throw new InvalidValveReportException(e.State),
        };
        var reportedAt = DateTime.SpecifyKind(e.ReportedAt, DateTimeKind.Utc);
        var now = clock.GetUtcNow().UtcDateTime;

        var valve = await valves.GetByPlaceAsync(e.PlaceId, ct);
        if (valve is not null && valve.DeviceId != e.DeviceId)
            return;   // a late report of a device that is no longer the one of this place: never swap the valve

        if (valve is null)
        {
            // The "device.linked" event was lost or comes later: the report itself proves the link.
            // Only if this device has no valve elsewhere (a late report after moving it must not bring it back here).
            if (await valves.GetByDeviceAsync(e.DeviceId, ct) is not null) return;
            valve = PlaceValve.ForNewDevice(e.PlaceId, e.DeviceId);
            await valves.ReplaceAsync(valve, now, ct);
        }

        if (valve.ApplyReport(state, reportedAt)) await valves.SaveReportAsync(valve, now, ct);

        if (e.CommandId is { } commandId)
        {
            var command = await commands.GetByIdAsync(commandId, ct);
            if (command is not null && command.PlaceId == e.PlaceId && command.Acknowledge(state, reportedAt))
                await commands.SaveOutcomeAsync(command, ct);
        }
    }
}
