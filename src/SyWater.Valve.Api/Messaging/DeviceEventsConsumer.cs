using System.Text.Json;
using Microsoft.Extensions.Options;
using SyWater.Valve.Application.Events;
using SyWater.Valve.Application.Ports.In;

namespace SyWater.Valve.Api.Messaging;

/// <summary>
/// Queue "valve.device-events": device.linked / device.unlinked (create or remove the valve of the place)
/// and device.valve.reported (what the ESP32 confirmed, which also closes the order it obeyed).
/// </summary>
public sealed class DeviceEventsConsumer(
    IServiceScopeFactory scopes,
    IOptions<RabbitMqOptions> options,
    ILogger<DeviceEventsConsumer> logger) : RabbitMqEventConsumer(options, logger)
{
    protected override string QueueName => "valve.device-events";

    protected override IReadOnlyList<string> RoutingKeys =>
        [DeviceLinkedEvent.EventType, DeviceUnlinkedEvent.EventType, ValveReportedEvent.EventType];

    protected override async Task HandleAsync(string type, JsonElement data, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();   // one DbContext per message
        var handler = scope.ServiceProvider.GetRequiredService<IDeviceEventsHandler>();

        switch (type)
        {
            case DeviceLinkedEvent.EventType:
                var linked = Read<DeviceLinkedEvent>(data);
                RequireIds(linked.DeviceId, linked.PlaceId);
                await handler.HandleAsync(linked, ct);
                break;
            case DeviceUnlinkedEvent.EventType:
                var unlinked = Read<DeviceUnlinkedEvent>(data);
                RequireIds(unlinked.DeviceId, unlinked.PlaceId);
                await handler.HandleAsync(unlinked, ct);
                break;
            case ValveReportedEvent.EventType:
                var reported = Read<ValveReportedEvent>(data);
                RequireIds(reported.DeviceId, reported.PlaceId);
                await handler.HandleAsync(reported, ct);
                break;
            default:
                logger.LogDebug("Event {Type} ignored", type);
                break;
        }
    }

    /// <summary>A missing id deserializes as Guid.Empty: that event can never be processed (dead-letter it).</summary>
    private static void RequireIds(Guid deviceId, Guid placeId)
    {
        if (deviceId == Guid.Empty || placeId == Guid.Empty) throw new JsonException("Event without deviceId/placeId.");
    }

    private T Read<T>(JsonElement data) => data.Deserialize<T>(Json) ?? throw new JsonException($"Empty {typeof(T).Name}.");
}
