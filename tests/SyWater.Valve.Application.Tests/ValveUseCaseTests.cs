using SyWater.Valve.Application.Events;
using SyWater.Valve.Application.Ports.Out;
using SyWater.Valve.Application.UseCases;
using SyWater.Valve.Application.Views;
using SyWater.Valve.Domain.Valves;

namespace SyWater.Valve.Application.Tests;

public class ValveUseCaseTests
{
    private readonly FakeClock _clock = new(new DateTime(2026, 10, 3, 15, 0, 0, DateTimeKind.Utc));
    private readonly FakePlaces _places = new();
    private readonly FakeValves _valves = new();
    private readonly FakeCommands _commands = new();
    private readonly FakeCodes _codes = new();
    private readonly FakeGateway _gateway;
    private readonly Guid _place = Guid.NewGuid();
    private readonly Guid _device = Guid.NewGuid();
    private readonly Requester _me = new(Guid.NewGuid(), "Juan Ome");

    public ValveUseCaseTests()
    {
        _gateway = new FakeGateway(_clock);
        _places.Mine.Add(_place);
    }

    private RequestValveCommandUseCase Request() =>
        new(_places, _valves, _commands, _codes, _gateway, ValvePolicy.Default, _clock);

    private DeviceEventsHandler Events() => new(_valves, _commands, _clock);

    private async Task LinkedAndReported(ValveState state)
    {
        await Events().HandleAsync(new DeviceLinkedEvent(_device, "SW-ESP32-000001", _place, _me.UserId, _clock.Now), default);
        await Events().HandleAsync(new ValveReportedEvent(_device, _place, state.ToString().ToUpperInvariant(), null, _clock.Now), default);
    }

    [Fact]
    public async Task Close_with_the_right_code_is_sent_and_confirmed_by_the_device_report()
    {
        await LinkedAndReported(ValveState.Open);

        var command = await Request().ExecuteAsync(_me, _place, ValveAction.Close, " 123456 ", default);
        Assert.Equal(CommandStatus.Sent, command.Status);
        Assert.Single(_gateway.Sent);
        Assert.Equal("Juan Ome", command.RequestedByName);

        _clock.Now = _clock.Now.AddSeconds(3);
        await Events().HandleAsync(new ValveReportedEvent(_device, _place, "CLOSED", command.Id, _clock.Now), default);

        var view = await new GetValveUseCase(_places, _valves, _commands, ValvePolicy.Default, _clock).ExecuteAsync(_place, default);
        Assert.Equal(ValveState.Closed, view.State);
        Assert.Equal(ValveCommunication.Ok, view.Communication);
        Assert.Null(view.PendingCommand);
        Assert.Equal(CommandStatus.AckSuccess, _commands.All.Single().Status);
    }

    [Fact]
    public async Task Close_without_or_with_a_wrong_code_is_rejected_and_nothing_is_sent()
    {
        await LinkedAndReported(ValveState.Open);
        await Assert.ThrowsAsync<ConfirmationCodeRequiredException>(() => Request().ExecuteAsync(_me, _place, ValveAction.Close, "", default));
        var rejected = await Assert.ThrowsAsync<ConfirmationCodeRejectedException>(() =>
            Request().ExecuteAsync(_me, _place, ValveAction.Close, "999999", default));
        Assert.Equal("auth.invalid_code", rejected.Code);
        Assert.Equal(4, rejected.RemainingAttempts);
        Assert.Empty(_gateway.Sent);
        Assert.Empty(_commands.All);
    }

    [Fact]
    public async Task Cheap_checks_run_before_the_code_is_spent()
    {
        await LinkedAndReported(ValveState.Closed);
        await Assert.ThrowsAsync<ValveAlreadyInStateException>(() => Request().ExecuteAsync(_me, _place, ValveAction.Close, "123456", default));
        Assert.Empty(_codes.Checked);

        await Assert.ThrowsAsync<PlaceNotFoundException>(() => Request().ExecuteAsync(_me, Guid.NewGuid(), ValveAction.Open, null, default));
    }

    [Fact]
    public async Task A_second_order_while_one_is_in_progress_is_a_conflict_until_it_times_out()
    {
        await LinkedAndReported(ValveState.Closed);
        var first = await Request().ExecuteAsync(_me, _place, ValveAction.Open, null, default);
        await Assert.ThrowsAsync<CommandInProgressException>(() => Request().ExecuteAsync(_me, _place, ValveAction.Open, null, default));

        _clock.Now = _clock.Now.AddSeconds(31);
        Assert.Equal(1, await new TimeOutCommandsUseCase(_commands, _clock).ExecuteAsync(default));
        Assert.Equal(CommandStatus.AckTimeout, _commands.All.Single(c => c.Id == first.Id).Status);

        var second = await Request().ExecuteAsync(_me, _place, ValveAction.Open, null, default);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public async Task Offline_device_marks_the_order_failed_and_rethrows()
    {
        await LinkedAndReported(ValveState.Closed);
        _gateway.Throw = new ValveDeviceOfflineException();
        await Assert.ThrowsAsync<ValveDeviceOfflineException>(() => Request().ExecuteAsync(_me, _place, ValveAction.Open, null, default));
        Assert.Equal(CommandStatus.Failed, _commands.All.Single().Status);
    }

    [Fact]
    public async Task No_device_means_no_valve_and_unlinking_fails_the_pending_order()
    {
        await Assert.ThrowsAsync<ValveNotFoundException>(() => Request().ExecuteAsync(_me, _place, ValveAction.Open, null, default));

        await LinkedAndReported(ValveState.Closed);
        await Request().ExecuteAsync(_me, _place, ValveAction.Open, null, default);
        await Events().HandleAsync(new DeviceUnlinkedEvent(_device, "SW-ESP32-000001", _place, _me.UserId, _clock.Now), default);

        Assert.Empty(_valves.ByPlace);
        Assert.Equal(CommandStatus.Failed, _commands.All.Single().Status);
    }

    [Fact]
    public async Task A_report_without_a_linked_event_creates_the_valve_and_bad_states_are_rejected()
    {
        await Events().HandleAsync(new ValveReportedEvent(_device, _place, "open", null, _clock.Now), default);
        Assert.Equal(ValveState.Open, _valves.ByPlace[_place].State);

        await Assert.ThrowsAsync<InvalidValveReportException>(() =>
            Events().HandleAsync(new ValveReportedEvent(_device, _place, "HALF", null, _clock.Now), default));
    }

    [Fact]
    public async Task History_lists_newest_first_and_hides_other_places()
    {
        await LinkedAndReported(ValveState.Closed);
        var open = await Request().ExecuteAsync(_me, _place, ValveAction.Open, null, default);
        await Events().HandleAsync(new ValveReportedEvent(_device, _place, "OPEN", open.Id, _clock.Now.AddSeconds(1)), default);
        _clock.Now = _clock.Now.AddMinutes(1);
        var close = await Request().ExecuteAsync(_me, _place, ValveAction.Close, "123456", default);

        var list = await new ListValveCommandsUseCase(_places, _commands, _clock).ExecuteAsync(_place, null, null, default);
        Assert.Equal([close.Id, open.Id], list.Select(c => c.Id).ToArray());

        await Assert.ThrowsAsync<PlaceNotFoundException>(() =>
            new ListValveCommandsUseCase(_places, _commands, _clock).ExecuteAsync(Guid.NewGuid(), null, null, default));
    }

    [Fact]
    public async Task Place_service_down_is_a_service_unavailable()
    {
        _places.Down = true;
        await Assert.ThrowsAsync<ExternalServiceUnavailableException>(() =>
            new GetValveUseCase(_places, _valves, _commands, ValvePolicy.Default, _clock).ExecuteAsync(_place, default));
    }

    [Fact]
    public async Task An_uncertain_delivery_keeps_the_order_waiting_and_the_device_report_still_confirms_it()
    {
        await LinkedAndReported(ValveState.Closed);
        _gateway.Throw = new DeliveryUncertainException(new TaskCanceledException());
        var command = await Request().ExecuteAsync(_me, _place, ValveAction.Open, null, default);
        Assert.Equal(CommandStatus.Sent, command.Status);

        await Events().HandleAsync(new ValveReportedEvent(_device, _place, "OPEN", command.Id, _clock.Now.AddSeconds(2)), default);
        Assert.Equal(CommandStatus.AckSuccess, _commands.All.Single().Status);
    }

    [Fact]
    public async Task Closing_a_valve_without_communication_is_refused_before_spending_the_code()
    {
        await LinkedAndReported(ValveState.Open);
        _clock.Now = _clock.Now.AddMinutes(4);
        await Assert.ThrowsAsync<ValveDeviceOfflineException>(() => Request().ExecuteAsync(_me, _place, ValveAction.Close, "123456", default));
        Assert.Empty(_codes.Checked);
    }

    [Fact]
    public async Task A_late_report_of_another_device_does_not_replace_the_valve_of_the_place()
    {
        await LinkedAndReported(ValveState.Open);
        await Events().HandleAsync(new ValveReportedEvent(Guid.NewGuid(), _place, "CLOSED", null, _clock.Now.AddSeconds(5)), default);
        Assert.Equal(_device, _valves.ByPlace[_place].DeviceId);
        Assert.Equal(ValveState.Open, _valves.ByPlace[_place].State);
    }
}
