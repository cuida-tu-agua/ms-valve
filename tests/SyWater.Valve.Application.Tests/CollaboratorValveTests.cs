using SyWater.Valve.Application.Events;
using SyWater.Valve.Application.Ports.Out;
using SyWater.Valve.Application.UseCases;
using SyWater.Valve.Application.Views;
using SyWater.Valve.Domain.Valves;

namespace SyWater.Valve.Application.Tests;

/// <summary>HU-044/045: what a collaborator may do with the valve depends on the permissions the owner gave.</summary>
public class CollaboratorValveTests
{
    private readonly FakeClock _clock = new(new DateTime(2026, 10, 3, 15, 0, 0, DateTimeKind.Utc));
    private readonly FakePlaces _places = new();
    private readonly FakeValves _valves = new();
    private readonly FakeCommands _commands = new();
    private readonly FakeCodes _codes = new();
    private readonly FakeGateway _gateway;
    private readonly Guid _place = Guid.NewGuid();
    private readonly Guid _device = Guid.NewGuid();
    private readonly Requester _ana = new(Guid.NewGuid(), "Ana Gil");

    public CollaboratorValveTests() => _gateway = new FakeGateway(_clock);

    private RequestValveCommandUseCase Request() =>
        new(_places, _valves, _commands, _codes, _gateway, ValvePolicy.Default, _clock);

    private GetValveUseCase GetValve() => new(_places, _valves, _commands, ValvePolicy.Default, _clock);
    private GetValveCommandUseCase GetCommand() => new(_places, _commands, _clock);
    private ListValveCommandsUseCase List() => new(_places, _commands, _clock);

    private async Task Linked(ValveState state)
    {
        var events = new DeviceEventsHandler(_valves, _commands, _clock);
        await events.HandleAsync(new DeviceLinkedEvent(_device, "SW-ESP32-000001", _place, Guid.NewGuid(), _clock.Now), default);
        await events.HandleAsync(new ValveReportedEvent(_device, _place, state.ToString().ToUpperInvariant(), null, _clock.Now), default);
    }

    private void Grant(params string[] permissions) => _places.Shared[_place] = ["VIEW_CONSUMPTION", .. permissions];

    [Fact]
    public async Task A_collaborator_with_CONTROL_VALVE_opens_and_closes()
    {
        await Linked(ValveState.Closed);
        Grant("CONTROL_VALVE");

        var opened = await Request().ExecuteAsync(_ana, _place, ValveAction.Open, null, default);

        Assert.Equal(CommandStatus.Sent, opened.Status);
        Assert.Equal("Ana Gil", opened.RequestedByName);   // the history says who did it
        Assert.Equal((_place, opened.Id, ValveAction.Open), Assert.Single(_gateway.Sent));
    }

    [Fact]
    public async Task A_collaborator_who_only_sees_the_device_cannot_order_the_valve_and_gets_403()
    {
        await Linked(ValveState.Open);
        Grant("VIEW_DEVICE_STATUS");

        var ex = await Assert.ThrowsAsync<PermissionDeniedException>(
            () => Request().ExecuteAsync(_ana, _place, ValveAction.Close, "123456", default));

        Assert.Equal("permission.denied", ex.Code);
        Assert.Empty(_gateway.Sent);
        Assert.Empty(_codes.Checked);   // the code was not even looked at
    }

    [Fact]
    public async Task A_collaborator_with_only_consumption_cannot_see_the_valve_either()
    {
        await Linked(ValveState.Open);
        Grant();

        await Assert.ThrowsAsync<PermissionDeniedException>(() => GetValve().ExecuteAsync(_place, default));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => List().ExecuteAsync(_place, null, null, default));
    }

    [Fact]
    public async Task VIEW_DEVICE_STATUS_shows_the_valve_state_and_the_history()
    {
        await Linked(ValveState.Open);
        Grant("VIEW_DEVICE_STATUS");

        var view = await GetValve().ExecuteAsync(_place, default);
        var history = await List().ExecuteAsync(_place, null, null, default);

        Assert.Equal(ValveState.Open, view.State);
        Assert.Empty(history);
    }

    [Fact]
    public async Task A_stranger_gets_404_for_everything_and_a_revoked_collaborator_too()
    {
        await Linked(ValveState.Open);
        Grant("CONTROL_VALVE");
        _places.Shared.Remove(_place);   // the owner revoked the access

        await Assert.ThrowsAsync<PlaceNotFoundException>(() => GetValve().ExecuteAsync(_place, default));
        await Assert.ThrowsAsync<PlaceNotFoundException>(() => GetCommand().ExecuteAsync(_place, Guid.NewGuid(), default));
        await Assert.ThrowsAsync<PlaceNotFoundException>(
            () => Request().ExecuteAsync(_ana, _place, ValveAction.Close, "123456", default));
    }

    [Fact]
    public async Task The_owner_always_can()
    {
        await Linked(ValveState.Closed);
        _places.Mine.Add(_place);

        await GetValve().ExecuteAsync(_place, default);
        await Request().ExecuteAsync(_ana, _place, ValveAction.Open, null, default);
    }

    [Fact]
    public async Task When_ms_places_is_down_nobody_gets_in()
    {
        await Linked(ValveState.Closed);
        Grant("CONTROL_VALVE");
        _places.Down = true;

        await Assert.ThrowsAsync<ExternalServiceUnavailableException>(
            () => Request().ExecuteAsync(_ana, _place, ValveAction.Open, null, default));
    }
}
