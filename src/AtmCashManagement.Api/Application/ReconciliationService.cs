using AtmCashManagement.Api.Domain;
using AtmCashManagement.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AtmCashManagement.Api.Application;

public sealed class ReconciliationService(
    AppDbContext db,
    CassetteLedger ledger,
    TimeProvider clock,
    IOptions<CashOptions> options)
{
    private decimal Threshold => options.Value.ReconciliationApprovalThresholdTry;

    public async Task<ReconciliationDto> CreateAsync(Guid atmId, CreateReconciliationRequest request, string? user,
        CancellationToken ct)
    {
        var countedBy = Errors.RequireUser(user);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var atm = await db.LockAtmAsync(atmId, ct) ?? throw Errors.AtmNotFound(atmId);

        AtmStateMachine.EnsureOperationAllowed(atm.State, AtmOperation.Reconcile);

        var pending = await db.Reconciliations.AsNoTracking()
            .FirstOrDefaultAsync(r => r.AtmId == atmId && r.Status == ReconciliationStatus.AwaitingApproval, ct);
        if (pending is not null)
        {
            throw new DomainException(ErrorCodes.ReconciliationPending,
                $"Reconciliation {pending.Id} is still waiting for approval. Approve or reject it first.",
                409,
                new Dictionary<string, object?> { ["reconciliationId"] = pending.Id });
        }

        var rates = await db.ExchangeRates.AsNoTracking().ToDictionaryAsync(r => r.Currency, r => r.RateToTry, ct);
        var lines = BuildLines(atm, request, rates);

        var now = clock.GetUtcNow();
        var total = lines.Sum(l => l.DifferenceValueTry);
        var reconciliation = new Reconciliation
        {
            Id = Guid.NewGuid(),
            AtmId = atm.Id,
            Status = total > Threshold ? ReconciliationStatus.AwaitingApproval : ReconciliationStatus.Completed,
            TotalDifferenceTry = total,
            CreatedBy = countedBy,
            CreatedAt = now,
            Lines = lines
        };

        // The physical count is a fact, so the system balance follows it straight away.
        // Approval does not decide what is in the cassettes; it decides whether the ATM may
        // go back into service with a difference of this size.
        foreach (var line in lines.Where(l => l.Difference != 0))
        {
            var cassette = atm.Cassettes.Single(c => c.Id == line.CassetteId);
            ledger.Record(atm, cassette, line.Currency, line.Denomination, MovementType.ReconciliationAdjustment,
                line.Difference, 0, "Reconciliation", reconciliation.Id, countedBy);
        }

        db.Reconciliations.Add(reconciliation);
        atm.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return reconciliation.ToDto(Threshold);
    }

    public Task<ReconciliationDto> ApproveAsync(Guid atmId, Guid reconciliationId, string? user, CancellationToken ct) =>
        DecideAsync(atmId, reconciliationId, user, ReconciliationStatus.Approved, ct);

    public Task<ReconciliationDto> RejectAsync(Guid atmId, Guid reconciliationId, string? user, CancellationToken ct) =>
        DecideAsync(atmId, reconciliationId, user, ReconciliationStatus.Rejected, ct);

    public async Task<ReconciliationDto> GetAsync(Guid atmId, Guid reconciliationId, CancellationToken ct)
    {
        var reconciliation = await db.Reconciliations.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == reconciliationId && r.AtmId == atmId, ct) ?? throw NotFound(reconciliationId);

        return reconciliation.ToDto(Threshold);
    }

    public async Task<IReadOnlyList<ReconciliationDto>> ListAsync(Guid atmId, CancellationToken ct)
    {
        var reconciliations = await db.Reconciliations.AsNoTracking()
            .Where(r => r.AtmId == atmId)
            .OrderByDescending(r => r.CreatedAt)
            .Take(100)
            .ToListAsync(ct);

        return reconciliations.Select(r => r.ToDto(Threshold)).ToList();
    }

    private async Task<ReconciliationDto> DecideAsync(Guid atmId, Guid reconciliationId, string? user,
        ReconciliationStatus decision, CancellationToken ct)
    {
        var decidedBy = Errors.RequireUser(user);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        _ = await db.LockAtmAsync(atmId, ct) ?? throw Errors.AtmNotFound(atmId);

        var reconciliation = await db.Reconciliations
            .FirstOrDefaultAsync(r => r.Id == reconciliationId && r.AtmId == atmId, ct) ?? throw NotFound(reconciliationId);

        if (reconciliation.Status != ReconciliationStatus.AwaitingApproval)
        {
            throw new DomainException(ErrorCodes.ReconciliationNotAwaitingApproval,
                $"The reconciliation is {EnumText.Of(reconciliation.Status)} and is not waiting for a decision.");
        }

        // Four-eyes principle: whoever counted the cash cannot sign off their own difference.
        if (string.Equals(reconciliation.CreatedBy, decidedBy, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainException(ErrorCodes.FourEyesViolation,
                $"The reconciliation was created by '{reconciliation.CreatedBy}' and must be decided by a different user.",
                403);
        }

        reconciliation.Status = decision;
        reconciliation.DecidedBy = decidedBy;
        reconciliation.DecidedAt = clock.GetUtcNow();

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return reconciliation.ToDto(Threshold);
    }

    private static List<ReconciliationLine> BuildLines(Atm atm, CreateReconciliationRequest request,
        IReadOnlyDictionary<Currency, decimal> rates)
    {
        var problems = new List<string>();
        var bySlot = request.Cassettes.GroupBy(c => c.Slot).ToDictionary(g => g.Key, g => g.ToList());
        var lines = new List<ReconciliationLine>();

        foreach (var cassette in atm.Cassettes.OrderBy(c => c.Slot))
        {
            if (!bySlot.TryGetValue(cassette.Slot, out var entries) || entries.Count != 1)
            {
                problems.Add($"Slot {cassette.Slot}: exactly one count entry is required.");
                continue;
            }

            var entry = entries[0];
            if (cassette.Type == CassetteType.Recycling)
            {
                if (entry.CountedNotes is null || entry.DepositCounts is not null)
                {
                    problems.Add($"Slot {cassette.Slot}: a recycling cassette needs countedNotes (and no depositCounts).");
                    continue;
                }

                lines.Add(Line(cassette, cassette.Currency!.Value, cassette.Denomination!.Value, entry.CountedNotes.Value));
                continue;
            }

            if (entry.DepositCounts is null || entry.CountedNotes is not null)
            {
                problems.Add($"Slot {cassette.Slot}: the deposit cassette needs depositCounts (and no countedNotes).");
                continue;
            }

            var counted = entry.DepositCounts;
            if (counted.Any(c => !Banknotes.IsSupported(c.Denomination)) ||
                counted.GroupBy(c => (c.Currency, c.Denomination)).Any(g => g.Count() > 1))
            {
                problems.Add($"Slot {cassette.Slot}: depositCounts must list each supported currency/denomination at most once.");
                continue;
            }

            if (counted.Sum(c => c.Count) > cassette.Capacity)
            {
                problems.Add($"Slot {cassette.Slot}: the counted total exceeds the cassette capacity of {cassette.Capacity}.");
                continue;
            }

            // Compare every kind of note either side knows about; a kind missing from the count is zero.
            var kinds = cassette.Stocks.Where(s => s.Count > 0).Select(s => (s.Currency, s.Denomination))
                .Union(counted.Select(c => (c.Currency!.Value, c.Denomination)))
                .OrderBy(k => k.Item1).ThenBy(k => k.Item2);

            foreach (var (currency, denomination) in kinds)
            {
                var countedNotes = counted.FirstOrDefault(c => c.Currency == currency && c.Denomination == denomination)?.Count ?? 0;
                lines.Add(Line(cassette, currency, denomination, countedNotes));
            }
        }

        var unknownSlots = bySlot.Keys.Where(slot => atm.GetCassette(slot) is null).ToList();
        if (unknownSlots.Count > 0)
        {
            problems.Add($"Unknown slot(s): {string.Join(", ", unknownSlots)}.");
        }

        if (problems.Count > 0)
        {
            throw new DomainException(ErrorCodes.ReconciliationIncomplete,
                "The count must cover each cassette of the ATM exactly once.",
                422,
                new Dictionary<string, object?> { ["violations"] = problems });
        }

        return lines;

        ReconciliationLine Line(Cassette cassette, Currency currency, int denomination, int countedNotes)
        {
            if (!rates.TryGetValue(currency, out var rate))
            {
                throw new DomainException(ErrorCodes.ExchangeRateMissing,
                    $"No exchange rate is defined for {currency}; the difference cannot be valued in TRY.", 422);
            }

            var systemCount = cassette.GetStock(currency, denomination)?.Count ?? 0;
            var difference = countedNotes - systemCount;
            return new ReconciliationLine
            {
                CassetteId = cassette.Id,
                Slot = cassette.Slot,
                Currency = currency,
                Denomination = denomination,
                SystemCount = systemCount,
                CountedCount = countedNotes,
                Difference = difference,
                RateToTry = rate,
                // Absolute value: a shortage in one cassette must not be hidden by a surplus in another.
                DifferenceValueTry = Math.Abs(difference) * denomination * rate
            };
        }
    }

    private static DomainException NotFound(Guid reconciliationId) =>
        new(ErrorCodes.ReconciliationNotFound, $"Reconciliation {reconciliationId} was not found for this ATM.", 404);
}
