using SyWater.Valve.Application.Ports.Out;
using SyWater.Valve.Domain.Valves;

namespace SyWater.Valve.Application.Tests;

public sealed class FakeClock(DateTime start) : TimeProvider
{
    public DateTime Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
}

public sealed class FakePlaces : IPlaceAccessChecker
{
    public HashSet<Guid> Mine { get; } = [];
    public bool Down { get; set; }
    public Task<bool> CanManageAsync(Guid placeId, CancellationToken ct) =>
        Down ? throw new ExternalServiceUnavailableException("place-service") : Task.FromResult(Mine.Contains(placeId));
}

public sealed class FakeValves : IValveRepository
{
    public Dictionary<Guid, PlaceValve> ByPlace { get; } = [];
    public Task<PlaceValve?> GetByPlaceAsync(Guid placeId, CancellationToken ct) => Task.FromResult(ByPlace.GetValueOrDefault(placeId));
    public Task<PlaceValve?> GetByDeviceAsync(Guid deviceId, CancellationToken ct) =>
        Task.FromResult(ByPlace.Values.FirstOrDefault(v => v.DeviceId == deviceId));
    public Task ReplaceAsync(PlaceValve valve, DateTime now, CancellationToken ct)
    {
        foreach (var key in ByPlace.Where(kv => kv.Value.DeviceId == valve.DeviceId).Select(kv => kv.Key).ToList()) ByPlace.Remove(key);
        ByPlace[valve.PlaceId] = valve;
        return Task.CompletedTask;
    }
    public Task RemoveAsync(Guid placeId, Guid deviceId, CancellationToken ct)
    {
        if (ByPlace.TryGetValue(placeId, out var v) && v.DeviceId == deviceId) ByPlace.Remove(placeId);
        return Task.CompletedTask;
    }
    public Task SaveReportAsync(PlaceValve valve, DateTime now, CancellationToken ct)
    {
        ByPlace[valve.PlaceId] = valve;
        return Task.CompletedTask;
    }
}

public sealed class FakeCommands : IValveCommandRepository
{
    public List<ValveCommand> All { get; } = [];
    public Task AddAsync(ValveCommand command, CancellationToken ct)
    {
        if (All.Any(c => c.PlaceId == command.PlaceId && c.IsPending)) throw new CommandInProgressException(command.PlaceId);
        All.Add(command);
        return Task.CompletedTask;
    }
    public Task<ValveCommand?> GetAsync(Guid placeId, Guid commandId, CancellationToken ct) =>
        Task.FromResult(All.FirstOrDefault(c => c.Id == commandId && c.PlaceId == placeId));
    public Task<ValveCommand?> GetByIdAsync(Guid commandId, CancellationToken ct) => Task.FromResult(All.FirstOrDefault(c => c.Id == commandId));
    public Task<ValveCommand?> GetPendingAsync(Guid placeId, CancellationToken ct) =>
        Task.FromResult(All.FirstOrDefault(c => c.PlaceId == placeId && c.IsPending));
    public Task<bool> SaveOutcomeAsync(ValveCommand command, CancellationToken ct) => Task.FromResult(true);   // same instances
    public Task SaveSentAtAsync(ValveCommand command, CancellationToken ct) => Task.CompletedTask;
    public Task<IReadOnlyList<ValveCommand>> ListAsync(Guid placeId, DateTime fromUtc, DateTime toUtc, int max, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ValveCommand>>(All.Where(c => c.PlaceId == placeId && c.CreatedAt >= fromUtc && c.CreatedAt < toUtc)
            .OrderByDescending(c => c.CreatedAt).Take(max).ToList());
    public Task<IReadOnlyList<ValveCommand>> GetExpiredAsync(DateTime now, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ValveCommand>>(All.Where(c => c.IsPending && c.TimeoutAt <= now).ToList());
}

public sealed class FakeCodes : IConfirmationCodeVerifier
{
    public string ValidCode { get; set; } = "123456";
    public List<string> Checked { get; } = [];
    public Task VerifyAsync(string action, string code, CancellationToken ct)
    {
        Checked.Add(code);
        return code == ValidCode ? Task.CompletedTask : throw new ConfirmationCodeRejectedException("auth.invalid_code", 4);
    }
}

public sealed class FakeGateway(FakeClock clock) : IDeviceGateway
{
    public Exception? Throw { get; set; }
    public List<(Guid PlaceId, Guid CommandId, ValveAction Action)> Sent { get; } = [];
    public Task<DateTime> SendAsync(Guid placeId, Guid commandId, ValveAction action, CancellationToken ct)
    {
        if (Throw is not null) throw Throw;
        Sent.Add((placeId, commandId, action));
        return Task.FromResult(clock.Now);
    }
}
