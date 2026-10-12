using System.Text.Json;
using AtmCashManagement.Api.Domain;
using AtmCashManagement.Api.Infrastructure;

namespace AtmCashManagement.Api.Application;

public sealed class DepositService(AppDbContext db, CassetteLedger ledger, TimeProvider clock)
{
    public const string CassettesFull = "CASSETTES_FULL";

    public async Task<DepositDto> DepositAsync(Guid atmId, DepositRequest request, CancellationToken ct)
    {
        if (request.Notes.Count == 0 && request.UnrecognisedCount == 0)
        {
            throw Errors.Validation("A deposit must contain at least one note.");
        }

        var unsupported = request.Notes.Where(n => !Banknotes.IsSupported(n.Denomination)).ToList();
        if (unsupported.Count > 0)
        {
            throw Errors.Validation(
                $"Unsupported denomination(s): {string.Join(", ", unsupported.Select(n => $"{n.Currency} {n.Denomination}"))}. " +
                $"Supported: {string.Join(", ", Banknotes.Denominations)}. Notes the machine cannot identify belong in unrecognisedCount.");
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var atm = await db.LockAtmAsync(atmId, ct) ?? throw Errors.AtmNotFound(atmId);

        AtmStateMachine.EnsureOperationAllowed(atm.State, AtmOperation.Deposit);

        var now = clock.GetUtcNow();
        var depositId = Guid.NewGuid();
        var depositCassette = atm.GetDepositCassette()!;
        var allocations = new List<DepositAllocationDto>();
        var returned = new List<ReturnedNotesDto>();

        // The same kind of note may appear on several lines; handle each kind once, in request order.
        var groups = request.Notes
            .GroupBy(n => (Currency: n.Currency!.Value, n.Denomination))
            .Select(g => (g.Key.Currency, g.Key.Denomination, Count: g.Sum(n => n.Count)));

        foreach (var (currency, denomination, count) in groups)
        {
            // 1. Matching recycling cassette(s), so the notes can be paid out again.
            var targets = atm.GetRecyclingCassettes(currency, denomination)
                .Select(c => new CassetteAllocator.Target(c.Id, c.Slot, c.TotalCount(), c.Capacity))
                .ToList();
            var (stored, overflow) = CassetteAllocator.Store(targets, count);

            foreach (var (cassetteId, storedCount) in stored)
            {
                var cassette = atm.Cassettes.Single(c => c.Id == cassetteId);
                ledger.Record(atm, cassette, currency, denomination, MovementType.Deposit, storedCount, 0,
                    "Deposit", depositId);
                allocations.Add(new DepositAllocationDto(currency, denomination, storedCount, cassette.Slot, cassette.Type));
            }

            // 2. No matching cassette, or it is full: the deposit cassette takes the rest.
            var toDepositCassette = Math.Min(overflow, depositCassette.FreeSpace());
            if (toDepositCassette > 0)
            {
                ledger.Record(atm, depositCassette, currency, denomination, MovementType.Deposit, toDepositCassette, 0,
                    "Deposit", depositId);
                allocations.Add(new DepositAllocationDto(currency, denomination, toDepositCassette,
                    depositCassette.Slot, depositCassette.Type));
            }

            // 3. Nothing has room: hand the notes back.
            var notAccepted = overflow - toDepositCassette;
            if (notAccepted > 0)
            {
                returned.Add(new ReturnedNotesDto(currency, denomination, notAccepted, CassettesFull));
            }
        }

        var totals = allocations
            .GroupBy(a => a.Currency)
            .OrderBy(g => g.Key)
            .Select(g => new DepositedTotalDto(g.Key, g.Sum(a => a.Count), g.Sum(a => (long)a.Count * a.Denomination)))
            .ToList();

        var result = new DepositDto(depositId, atm.Id, allocations, returned, request.UnrecognisedCount, totals, now);

        db.DepositTransactions.Add(new DepositTransaction
        {
            Id = depositId,
            AtmId = atm.Id,
            UnrecognisedCount = request.UnrecognisedCount,
            Result = JsonSerializer.Serialize(result, JsonDefaults.Options),
            CreatedAt = now
        });

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return result;
    }
}
