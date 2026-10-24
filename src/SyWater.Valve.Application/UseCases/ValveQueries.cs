using SyWater.Valve.Application.Ports.In;
using SyWater.Valve.Application.Ports.Out;
using SyWater.Valve.Application.Views;
using SyWater.Valve.Domain.Valves;

namespace SyWater.Valve.Application.UseCases;

/// <summary>HU-019. Someone else's place → 404, like a place without device. A collaborator needs VIEW_DEVICE_STATUS or CONTROL_VALVE.</summary>
public sealed class GetValveUseCase(
    IPlaceAccessChecker places, IValveRepository valves, IValveCommandRepository commands, ValvePolicy policy, TimeProvider clock)
    : IGetValveUseCase
{
    public async Task<ValveView> ExecuteAsync(Guid placeId, CancellationToken ct)
    {
        await places.RequireAsync(placeId, PlaceAccessRules.CanSee, ct);
        var valve = await valves.GetByPlaceAsync(placeId, ct) ?? throw new ValveNotFoundException(placeId);

        var now = clock.GetUtcNow().UtcDateTime;
        var pending = await commands.GetPendingAsync(placeId, ct);
        if (pending is not null && pending.TimeOutIfExpired(now))
        {
            await commands.SaveOutcomeAsync(pending, ct);
            pending = null;
        }

        return new ValveView(placeId, valve.State, valve.CommunicationAt(now, policy), valve.LastConfirmedAt,
            pending is null ? null : ValveCommandView.From(pending));
    }
}

/// <summary>Progress of one order. Applies the 30 s timeout right away, without waiting for the sweeper.</summary>
public sealed class GetValveCommandUseCase(IPlaceAccessChecker places, IValveCommandRepository commands, TimeProvider clock)
    : IGetValveCommandUseCase
{
    public async Task<ValveCommandView> ExecuteAsync(Guid placeId, Guid commandId, CancellationToken ct)
    {
        await places.RequireAsync(placeId, PlaceAccessRules.CanSee, ct);
        var command = await commands.GetAsync(placeId, commandId, ct) ?? throw new CommandNotFoundException(commandId);

        if (command.TimeOutIfExpired(clock.GetUtcNow().UtcDateTime) && !await commands.SaveOutcomeAsync(command, ct))
            command = await commands.GetAsync(placeId, commandId, ct) ?? command;   // the device confirmed just in time

        return ValveCommandView.From(command);
    }
}

/// <summary>HU-022: who opened or closed the water and when. Default range: the last 30 days.</summary>
public sealed class ListValveCommandsUseCase(IPlaceAccessChecker places, IValveCommandRepository commands, TimeProvider clock)
    : IListValveCommandsUseCase
{
    public const int MaxItems = 200;

    public async Task<IReadOnlyList<ValveCommandView>> ExecuteAsync(Guid placeId, DateTime? fromUtc, DateTime? toUtc, CancellationToken ct)
    {
        await places.RequireAsync(placeId, PlaceAccessRules.CanSee, ct);

        var now = clock.GetUtcNow().UtcDateTime;
        var to = toUtc ?? now.AddMinutes(1);
        var from = fromUtc ?? to.AddDays(-30);
        if (from > to) (from, to) = (to, from);

        var list = await commands.ListAsync(placeId, from, to, MaxItems, ct);
        return list.Select(ValveCommandView.From).ToList();
    }
}

/// <summary>HU-020: every few seconds, SENT orders past their 30 s become ACK_TIMEOUT.</summary>
public sealed class TimeOutCommandsUseCase(IValveCommandRepository commands, TimeProvider clock) : ITimeOutCommandsUseCase
{
    public async Task<int> ExecuteAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var count = 0;
        foreach (var command in await commands.GetExpiredAsync(now, ct))
            if (command.TimeOutIfExpired(now) && await commands.SaveOutcomeAsync(command, ct)) count++;
        return count;
    }
}
