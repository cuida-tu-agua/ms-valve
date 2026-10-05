using SyWater.Valve.Domain.Valves;

namespace SyWater.Valve.Domain.Tests;

public class ValveDomainTests
{
    private static readonly DateTime Now = new(2026, 10, 3, 15, 0, 0, DateTimeKind.Utc);
    private static readonly ValvePolicy Policy = ValvePolicy.Default;

    [Fact]
    public void A_new_valve_is_unknown_and_without_communication()
    {
        var valve = PlaceValve.ForNewDevice(Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(ValveState.Unknown, valve.State);
        Assert.Equal(ValveCommunication.NoCommunication, valve.CommunicationAt(Now, Policy));
    }

    [Fact]
    public void Reports_change_the_state_and_older_reports_are_ignored()
    {
        var valve = PlaceValve.ForNewDevice(Guid.NewGuid(), Guid.NewGuid());
        Assert.True(valve.ApplyReport(ValveState.Closed, Now));
        Assert.False(valve.ApplyReport(ValveState.Open, Now.AddSeconds(-10)));   // out of order
        Assert.Equal(ValveState.Closed, valve.State);
        Assert.Equal(ValveCommunication.Ok, valve.CommunicationAt(Now.AddMinutes(3), Policy));
        Assert.Equal(ValveCommunication.NoCommunication, valve.CommunicationAt(Now.AddMinutes(3).AddSeconds(1), Policy));
    }

    [Fact]
    public void Closing_by_hand_requires_the_code_opening_does_not()
    {
        Assert.Throws<ConfirmationCodeRequiredException>(() =>
            ValveCommand.Manual(Guid.NewGuid(), Guid.NewGuid(), ValveAction.Close, Guid.NewGuid(), "Ana", false, Now, Policy));

        var open = ValveCommand.Manual(Guid.NewGuid(), Guid.NewGuid(), ValveAction.Open, Guid.NewGuid(), "Ana", false, Now, Policy);
        Assert.Equal(CommandStatus.Sent, open.Status);
        Assert.Equal(Now.AddSeconds(30), open.TimeoutAt);
    }

    [Fact]
    public void The_matching_report_confirms_the_order_and_a_wrong_one_fails_it()
    {
        var close = ValveCommand.Manual(Guid.NewGuid(), Guid.NewGuid(), ValveAction.Close, Guid.NewGuid(), "Ana", true, Now, Policy);
        Assert.True(close.Acknowledge(ValveState.Closed, Now.AddSeconds(2)));
        Assert.Equal(CommandStatus.AckSuccess, close.Status);
        Assert.False(close.Acknowledge(ValveState.Open, Now.AddSeconds(3)));   // already finished

        var open = ValveCommand.Manual(Guid.NewGuid(), Guid.NewGuid(), ValveAction.Open, Guid.NewGuid(), null, false, Now, Policy);
        Assert.True(open.Acknowledge(ValveState.Closed, Now.AddSeconds(2)));
        Assert.Equal(CommandStatus.Failed, open.Status);
    }

    [Fact]
    public void Thirty_seconds_without_answer_is_a_timeout_and_a_late_report_does_not_revive_it()
    {
        var open = ValveCommand.Manual(Guid.NewGuid(), Guid.NewGuid(), ValveAction.Open, Guid.NewGuid(), null, false, Now, Policy);
        Assert.False(open.TimeOutIfExpired(Now.AddSeconds(29)));
        Assert.True(open.TimeOutIfExpired(Now.AddSeconds(30)));
        Assert.Equal(CommandStatus.AckTimeout, open.Status);
        Assert.False(open.Acknowledge(ValveState.Open, Now.AddSeconds(31)));
        Assert.Equal(CommandStatus.AckTimeout, open.Status);
    }
}
