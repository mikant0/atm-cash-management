using AtmCashManagement.Api.Domain;
using AtmCashManagement.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AtmCashManagement.Api.Application;

/// <summary>ATM registration, cassette configuration, cash loading and lifecycle changes.</summary>
public sealed class AtmService(
    AppDbContext db,
    CassetteLedger ledger,
    WithdrawalService withdrawals,
    TimeProvider clock)
{
    public async Task<AtmDto> CreateAsync(CreateAtmRequest request, CancellationToken ct)
    {
        var code = request.Code.Trim();
        if (await db.Atms.AnyAsync(a => a.Code == code, ct))
        {
            throw new DomainException(ErrorCodes.AtmCodeExists, $"An ATM with code '{code}' already exists.");
        }

        var now = clock.GetUtcNow();
        var atm = new Atm
        {
            Id = Guid.NewGuid(),
            Code = code,
            Location = request.Location,
            State = AtmState.Unconfigured,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.Atms.Add(atm);
        await db.SaveChangesAsync(ct);
        return atm.ToDto();
    }

    public async Task<IReadOnlyList<AtmDto>> ListAsync(CancellationToken ct)
    {
        var atms = await db.Atms.AsNoTracking()
            .Include(a => a.Cassettes).ThenInclude(c => c.Stocks)
            .OrderBy(a => a.Code)
            .AsSplitQuery()
            .ToListAsync(ct);

        return atms.Select(a => a.ToDto()).ToList();
    }

    public async Task<AtmDto> GetAsync(Guid atmId, CancellationToken ct)
    {
        var atm = await db.Atms.AsNoTracking()
            .Include(a => a.Cassettes).ThenInclude(c => c.Stocks)
            .AsSplitQuery()
            .FirstOrDefaultAsync(a => a.Id == atmId, ct) ?? throw Errors.AtmNotFound(atmId);

        return atm.ToDto();
    }

    public async Task<AtmDto> ConfigureCassettesAsync(Guid atmId, ConfigureCassettesRequest request,
        CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var atm = await db.LockAtmAsync(atmId, ct) ?? throw Errors.AtmNotFound(atmId);

        AtmStateMachine.EnsureOperationAllowed(atm.State, AtmOperation.ConfigureCassettes);

        var specs = request.Cassettes
            .Select(c => new CassetteSpec(c.Slot, c.Type!.Value, c.Currency, c.Denomination))
            .ToList();
        CassetteLayoutRules.EnsureValid(specs);

        // Changing what a cassette holds while notes are still inside would make those notes
        // unaccounted for. They have to be unloaded (and recorded) first.
        var loaded = atm.Cassettes.Where(c => c.TotalCount() > 0).Select(c => c.Slot).ToArray();
        if (loaded.Length > 0)
        {
            throw new DomainException(ErrorCodes.CassetteNotEmpty,
                $"Cassettes in slots {string.Join(", ", loaded)} still contain notes. " +
                "Unload them before changing the configuration.",
                409,
                new Dictionary<string, object?> { ["slots"] = loaded });
        }

        foreach (var spec in specs)
        {
            var cassette = atm.GetCassette(spec.Slot);
            if (cassette is null)
            {
                cassette = new Cassette { Id = Guid.NewGuid(), AtmId = atm.Id, Slot = spec.Slot };
                atm.Cassettes.Add(cassette);
                db.Cassettes.Add(cassette);
            }

            cassette.Type = spec.Type;
            cassette.Currency = spec.Currency;
            cassette.Denomination = spec.Denomination;
            cassette.Capacity = Banknotes.CassetteCapacity;
        }

        if (atm.State == AtmState.Unconfigured)
        {
            atm.State = AtmState.Configured;
        }

        atm.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return atm.ToDto();
    }

    public async Task<AtmDto> LoadCashAsync(Guid atmId, LoadCashRequest request, string? user, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var atm = await db.LockAtmAsync(atmId, ct) ?? throw Errors.AtmNotFound(atmId);

        AtmStateMachine.EnsureOperationAllowed(atm.State, AtmOperation.LoadCash);

        var operationId = Guid.NewGuid();
        foreach (var item in request.Items.GroupBy(i => i.Slot).Select(g => (Slot: g.Key, Count: g.Sum(i => i.Count))))
        {
            var cassette = RequireCassette(atm, item.Slot);
            if (cassette.Type == CassetteType.Deposit)
            {
                throw new DomainException(ErrorCodes.DepositCassetteNotLoadable,
                    $"Slot {item.Slot} is the deposit cassette. It only receives customer deposits and cannot be loaded.",
                    422);
            }

            if (item.Count > cassette.FreeSpace())
            {
                throw new DomainException(ErrorCodes.CassetteCapacityExceeded,
                    $"Slot {item.Slot} holds {cassette.TotalCount()} of {cassette.Capacity} notes; " +
                    $"{item.Count} more do not fit.",
                    422,
                    new Dictionary<string, object?>
                    {
                        ["slot"] = item.Slot,
                        ["capacity"] = cassette.Capacity,
                        ["currentCount"] = cassette.TotalCount(),
                        ["requestedCount"] = item.Count
                    });
            }

            ledger.Record(atm, cassette, cassette.Currency!.Value, cassette.Denomination!.Value,
                MovementType.Load, item.Count, 0, "CashLoad", operationId, user);
        }

        if (atm.State == AtmState.Configured)
        {
            atm.State = AtmState.Loaded;
        }

        atm.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return atm.ToDto();
    }

    public async Task<AtmDto> UnloadCashAsync(Guid atmId, UnloadCashRequest request, string? user,
        CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var atm = await db.LockAtmAsync(atmId, ct) ?? throw Errors.AtmNotFound(atmId);

        AtmStateMachine.EnsureOperationAllowed(atm.State, AtmOperation.UnloadCash);

        var operationId = Guid.NewGuid();
        foreach (var slot in request.Slots.Distinct())
        {
            var cassette = RequireCassette(atm, slot);
            foreach (var stock in cassette.Stocks.Where(s => s.Count > 0).ToList())
            {
                ledger.Record(atm, cassette, stock.Currency, stock.Denomination,
                    MovementType.Unload, -stock.Count, 0, "CashUnload", operationId, user);
            }
        }

        atm.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return atm.ToDto();
    }

    public async Task<AtmDto> ChangeStateAsync(Guid atmId, AtmState target, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var atm = await db.LockAtmAsync(atmId, ct) ?? throw Errors.AtmNotFound(atmId);

        AtmStateMachine.EnsureTransition(atm.State, target);
        var now = clock.GetUtcNow();

        if (target == AtmState.InService)
        {
            await EnsureNoBlockingReconciliationAsync(atm, ct);
        }

        if (target == AtmState.Maintenance)
        {
            // Nobody can confirm a quote on a machine that is being serviced. Releasing the
            // reservations also guarantees that counts and reconfiguration see no held notes.
            await withdrawals.CancelActiveQuotesAsync(atm, now, ct);
        }

        atm.State = target;
        atm.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return atm.ToDto();
    }

    private async Task EnsureNoBlockingReconciliationAsync(Atm atm, CancellationToken ct)
    {
        var latest = await db.Reconciliations.AsNoTracking()
            .Where(r => r.AtmId == atm.Id)
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (latest?.Status is ReconciliationStatus.AwaitingApproval or ReconciliationStatus.Rejected)
        {
            var reason = latest.Status == ReconciliationStatus.AwaitingApproval
                ? "is waiting for approval by a second user"
                : "was rejected and the cash must be counted again";

            throw new DomainException(ErrorCodes.ReconciliationBlocksService,
                $"The ATM cannot return to service: reconciliation {latest.Id} {reason}.",
                409,
                new Dictionary<string, object?>
                {
                    ["reconciliationId"] = latest.Id,
                    ["reconciliationStatus"] = EnumText.Of(latest.Status)
                });
        }
    }

    private static Cassette RequireCassette(Atm atm, int slot) =>
        atm.GetCassette(slot) ?? throw new DomainException(ErrorCodes.CassetteNotFound,
            $"The ATM has no cassette in slot {slot}.", 404);
}
