using Microsoft.EntityFrameworkCore;
using SyWater.Valve.Application.Ports.Out;
using SyWater.Valve.Domain.Valves;
using SyWater.Valve.Infrastructure.Persistence.Entities;

namespace SyWater.Valve.Infrastructure.Persistence;

public sealed class EfValveRepository(ValveDbContext db) : IValveRepository
{
    public async Task<PlaceValve?> GetByPlaceAsync(Guid placeId, CancellationToken ct)
    {
        var row = await db.States.AsNoTracking().FirstOrDefaultAsync(v => v.PlaceId == placeId, ct);
        return row is null ? null : ValveMapper.ToDomain(row);
    }

    public async Task<PlaceValve?> GetByDeviceAsync(Guid deviceId, CancellationToken ct)
    {
        var row = await db.States.AsNoTracking().FirstOrDefaultAsync(v => v.DeviceId == deviceId, ct);
        return row is null ? null : ValveMapper.ToDomain(row);
    }

    /// <summary>DELETE the old row of that place or device + INSERT the new one, in one transaction.</summary>
    public async Task ReplaceAsync(PlaceValve valve, DateTime now, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.States.Where(v => v.PlaceId == valve.PlaceId || v.DeviceId == valve.DeviceId).ExecuteDeleteAsync(ct);
        db.States.Add(new ValveStateEntity
        {
            PlaceId = valve.PlaceId,
            DeviceId = valve.DeviceId,
            State = ValveMapper.ToDb(valve.State),
            LastConfirmedAt = valve.LastConfirmedAt,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        db.ChangeTracker.Clear();
    }

    public Task RemoveAsync(Guid placeId, Guid deviceId, CancellationToken ct) =>
        db.States.Where(v => v.PlaceId == placeId && v.DeviceId == deviceId).ExecuteDeleteAsync(ct);

    /// <summary>The WHERE keeps the newest report if two arrive out of order (RabbitMQ does not promise order).</summary>
    public Task SaveReportAsync(PlaceValve valve, DateTime now, CancellationToken ct) =>
        db.States
            .Where(v => v.PlaceId == valve.PlaceId && v.DeviceId == valve.DeviceId
                        && (v.LastConfirmedAt == null || v.LastConfirmedAt <= valve.LastConfirmedAt))
            .ExecuteUpdateAsync(s => s
                .SetProperty(v => v.State, ValveMapper.ToDb(valve.State))
                .SetProperty(v => v.LastConfirmedAt, valve.LastConfirmedAt)
                .SetProperty(v => v.UpdatedAt, now), ct);
}

public sealed class EfValveCommandRepository(ValveDbContext db) : IValveCommandRepository
{
    public async Task AddAsync(ValveCommand command, CancellationToken ct)
    {
        db.Commands.Add(ValveMapper.ToEntity(command));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            // UX_vcmd_place_pending: another phone sent an order for this place a moment ago
            if (await db.Commands.AnyAsync(c => c.PlaceId == command.PlaceId && c.Status == "SENT", ct))
                throw new CommandInProgressException(command.PlaceId);
            throw;
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    public async Task<ValveCommand?> GetAsync(Guid placeId, Guid commandId, CancellationToken ct)
    {
        var row = await db.Commands.AsNoTracking().FirstOrDefaultAsync(c => c.Id == commandId && c.PlaceId == placeId, ct);
        return row is null ? null : ValveMapper.ToDomain(row);
    }

    public async Task<ValveCommand?> GetByIdAsync(Guid commandId, CancellationToken ct)
    {
        var row = await db.Commands.AsNoTracking().FirstOrDefaultAsync(c => c.Id == commandId, ct);
        return row is null ? null : ValveMapper.ToDomain(row);
    }

    public async Task<ValveCommand?> GetPendingAsync(Guid placeId, CancellationToken ct)
    {
        var row = await db.Commands.AsNoTracking().FirstOrDefaultAsync(c => c.PlaceId == placeId && c.Status == "SENT", ct);
        return row is null ? null : ValveMapper.ToDomain(row);
    }

    public async Task<bool> SaveOutcomeAsync(ValveCommand command, CancellationToken ct) =>
        await db.Commands
            .Where(c => c.Id == command.Id && c.Status == "SENT")   // only the FIRST ending wins
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, ValveMapper.ToDb(command.Status))
                .SetProperty(c => c.AcknowledgedAt, command.AcknowledgedAt)
                .SetProperty(c => c.FailureReason, command.FailureReason), ct) == 1;

    public Task SaveSentAtAsync(ValveCommand command, CancellationToken ct) =>
        db.Commands.Where(c => c.Id == command.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.SentAt, command.SentAt), ct);

    public async Task<IReadOnlyList<ValveCommand>> ListAsync(Guid placeId, DateTime fromUtc, DateTime toUtc, int max, CancellationToken ct)
    {
        var rows = await db.Commands.AsNoTracking()
            .Where(c => c.PlaceId == placeId && c.CreatedAt >= fromUtc && c.CreatedAt < toUtc)
            .OrderByDescending(c => c.CreatedAt)
            .Take(max)
            .ToListAsync(ct);
        return rows.Select(ValveMapper.ToDomain).ToList();
    }

    public async Task<IReadOnlyList<ValveCommand>> GetExpiredAsync(DateTime now, CancellationToken ct)
    {
        var rows = await db.Commands.AsNoTracking()
            .Where(c => c.Status == "SENT" && c.TimeoutAt <= now)
            .ToListAsync(ct);
        return rows.Select(ValveMapper.ToDomain).ToList();
    }
}
