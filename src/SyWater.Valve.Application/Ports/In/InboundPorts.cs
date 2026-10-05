using SyWater.Valve.Application.Events;
using SyWater.Valve.Application.Views;
using SyWater.Valve.Domain.Valves;

namespace SyWater.Valve.Application.Ports.In;

/// <summary>HU-019</summary>
public interface IGetValveUseCase
{
    Task<ValveView> ExecuteAsync(Guid placeId, CancellationToken ct);
}

/// <summary>HU-020 (close, with code) and HU-021 (open).</summary>
public interface IRequestValveCommandUseCase
{
    Task<ValveCommandView> ExecuteAsync(Requester requester, Guid placeId, ValveAction action, string? code, CancellationToken ct);
}

/// <summary>Progress of one order (the app polls it while it waits for the device).</summary>
public interface IGetValveCommandUseCase
{
    Task<ValveCommandView> ExecuteAsync(Guid placeId, Guid commandId, CancellationToken ct);
}

/// <summary>HU-022</summary>
public interface IListValveCommandsUseCase
{
    Task<IReadOnlyList<ValveCommandView>> ExecuteAsync(Guid placeId, DateTime? fromUtc, DateTime? toUtc, CancellationToken ct);
}

/// <summary>Events from device-service (RabbitMQ).</summary>
public interface IDeviceEventsHandler
{
    Task HandleAsync(DeviceLinkedEvent e, CancellationToken ct);
    Task HandleAsync(DeviceUnlinkedEvent e, CancellationToken ct);
    Task HandleAsync(ValveReportedEvent e, CancellationToken ct);
}

/// <summary>HU-020: orders without confirmation after 30 s become ACK_TIMEOUT. Returns how many.</summary>
public interface ITimeOutCommandsUseCase
{
    Task<int> ExecuteAsync(CancellationToken ct);
}
