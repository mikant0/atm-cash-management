using AtmCashManagement.Api.Domain;
using AtmCashManagement.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AtmCashManagement.Api.Application;

/// <summary>Read-only views: inventory, operations report, movement history and the ledger consistency check.</summary>
public sealed class ReportService(AppDbContext db, TimeProvider clock)
{
    public async Task<InventoryDto> GetInventoryAsync(Guid atmId, CancellationToken ct)
    {
        var atm = await LoadAtms(atmId).FirstOrDefaultAsync(ct) ?? throw Errors.AtmNotFound(atmId);
        var rates = await LoadRatesAsync(ct);

        var notes = atm.Cassettes
            .SelectMany(c => c.Stocks.Select(s => (Cassette: c, Stock: s)))
            .Where(x => x.Stock.Count > 0)
            .GroupBy(x => (x.Stock.Currency, x.Stock.Denomination))
            .OrderBy(g => g.Key.Currency).ThenByDescending(g => g.Key.Denomination)
            .Select(g => new DenominationCountDto(
                g.Key.Currency,
                g.Key.Denomination,
                g.Sum(x => x.Stock.Count),
                g.Sum(x => x.Stock.Reserved),
                g.Where(x => x.Cassette.Type == CassetteType.Recycling).Sum(x => x.Stock.Count - x.Stock.Reserved),
                (long)g.Sum(x => x.Stock.Count) * g.Key.Denomination))
            .ToList();

        var totals = TotalsByCurrency([atm], rates);
        return new InventoryDto(atm.Id, atm.Code, atm.State, notes, totals, totals.Sum(t => t.ValueTry));
    }

    public async Task<OperationsReportDto> GetOperationsReportAsync(Guid? atmId, CancellationToken ct)
    {
        var atms = await LoadAtms(atmId).OrderBy(a => a.Code).ToListAsync(ct);
        if (atmId is not null && atms.Count == 0)
        {
            throw Errors.AtmNotFound(atmId.Value);
        }

        var rates = await LoadRatesAsync(ct);

        var atmReports = atms.Select(atm =>
        {
            var totals = TotalsByCurrency([atm], rates);
            return new AtmReportDto(atm.Id, atm.Code, atm.Location, atm.State,
                atm.Cassettes.OrderBy(c => c.Slot).Select(c => c.ToDto()).ToList(),
                totals, totals.Sum(t => t.ValueTry));
        }).ToList();

        var networkTotals = TotalsByCurrency(atms, rates);
        return new OperationsReportDto(clock.GetUtcNow(), rates, atmReports, networkTotals,
            networkTotals.Sum(t => t.ValueTry));
    }

    public async Task<IReadOnlyList<MovementDto>> GetMovementsAsync(Guid atmId, int? slot, int limit,
        CancellationToken ct)
    {
        if (!await db.Atms.AnyAsync(a => a.Id == atmId, ct))
        {
            throw Errors.AtmNotFound(atmId);
        }

        var query = db.CassetteMovements.AsNoTracking().Where(m => m.AtmId == atmId);
        if (slot is not null)
        {
            var cassetteIds = db.Cassettes.Where(c => c.AtmId == atmId && c.Slot == slot).Select(c => c.Id);
            query = query.Where(m => cassetteIds.Contains(m.CassetteId));
        }

        var movements = await query.OrderByDescending(m => m.Id).Take(Math.Clamp(limit, 1, 500)).ToListAsync(ct);
        return movements.Select(m => m.ToDto()).ToList();
    }

    /// <summary>
    /// Recalculates every cassette balance from the movement ledger and compares it with the stored
    /// balance. Both sides are read in one REPEATABLE READ transaction so that operations running at
    /// the same time cannot make a healthy system look inconsistent.
    /// </summary>
    public async Task<ConsistencyReportDto> CheckConsistencyAsync(Guid? atmId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);

        if (atmId is not null && !await db.Atms.AnyAsync(a => a.Id == atmId, ct))
        {
            throw Errors.AtmNotFound(atmId.Value);
        }

        var stored = await (
            from stock in db.CassetteStocks.AsNoTracking()
            join cassette in db.Cassettes.AsNoTracking() on stock.CassetteId equals cassette.Id
            join atm in db.Atms.AsNoTracking() on cassette.AtmId equals atm.Id
            where atmId == null || atm.Id == atmId
            select new
            {
                AtmId = atm.Id, AtmCode = atm.Code, CassetteId = cassette.Id, cassette.Slot,
                stock.Currency, stock.Denomination, stock.Count, stock.Reserved
            }).ToListAsync(ct);

        var calculated = await (
            from movement in db.CassetteMovements.AsNoTracking()
            join cassette in db.Cassettes.AsNoTracking() on movement.CassetteId equals cassette.Id
            join atm in db.Atms.AsNoTracking() on cassette.AtmId equals atm.Id
            where atmId == null || atm.Id == atmId
            group movement by new
            {
                AtmId = atm.Id, AtmCode = atm.Code, movement.CassetteId, cassette.Slot,
                movement.Currency, movement.Denomination
            }
            into g
            select new
            {
                g.Key.AtmId, g.Key.AtmCode, g.Key.CassetteId, g.Key.Slot, g.Key.Currency, g.Key.Denomination,
                Count = g.Sum(m => m.CountDelta), Reserved = g.Sum(m => m.ReservedDelta)
            }).ToListAsync(ct);

        await tx.CommitAsync(ct);

        var storedByKey = stored.ToDictionary(s => (s.CassetteId, s.Currency, s.Denomination));
        var calculatedByKey = calculated.ToDictionary(c => (c.CassetteId, c.Currency, c.Denomination));

        var differences = new List<LedgerDifferenceDto>();
        foreach (var key in storedByKey.Keys.Union(calculatedByKey.Keys))
        {
            storedByKey.TryGetValue(key, out var s);
            calculatedByKey.TryGetValue(key, out var c);

            var (storedCount, storedReserved) = (s?.Count ?? 0, s?.Reserved ?? 0);
            var (ledgerCount, ledgerReserved) = (c?.Count ?? 0, c?.Reserved ?? 0);
            if (storedCount == ledgerCount && storedReserved == ledgerReserved)
            {
                continue;
            }

            differences.Add(new LedgerDifferenceDto(
                s?.AtmId ?? c!.AtmId, s?.AtmCode ?? c!.AtmCode, key.CassetteId, s?.Slot ?? c!.Slot,
                key.Currency, key.Denomination, storedCount, ledgerCount, storedReserved, ledgerReserved));
        }

        var ordered = differences.OrderBy(d => d.AtmCode).ThenBy(d => d.Slot).ThenBy(d => d.Currency)
            .ThenBy(d => d.Denomination).ToList();

        return new ConsistencyReportDto(clock.GetUtcNow(), ordered.Count == 0,
            storedByKey.Keys.Union(calculatedByKey.Keys).Count(), ordered);
    }

    public async Task<IReadOnlyList<ExchangeRateDto>> GetExchangeRatesAsync(CancellationToken ct)
    {
        var rates = await db.ExchangeRates.AsNoTracking().OrderBy(r => r.Currency).ToListAsync(ct);
        return rates.Select(r => new ExchangeRateDto(r.Currency, r.RateToTry, r.UpdatedAt)).ToList();
    }

    public async Task<ExchangeRateDto> UpdateExchangeRateAsync(Currency currency, decimal rateToTry, CancellationToken ct)
    {
        if (currency == Currency.TRY && rateToTry != 1m)
        {
            throw Errors.Validation("TRY is the base currency; its rate is always 1.");
        }

        var rate = await db.ExchangeRates.FirstOrDefaultAsync(r => r.Currency == currency, ct);
        if (rate is null)
        {
            rate = new ExchangeRate { Currency = currency };
            db.ExchangeRates.Add(rate);
        }

        rate.RateToTry = rateToTry;
        rate.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return new ExchangeRateDto(rate.Currency, rate.RateToTry, rate.UpdatedAt);
    }

    private IQueryable<Atm> LoadAtms(Guid? atmId) =>
        db.Atms.AsNoTracking()
            .Include(a => a.Cassettes).ThenInclude(c => c.Stocks)
            .AsSplitQuery()
            .Where(a => atmId == null || a.Id == atmId);

    private async Task<Dictionary<Currency, decimal>> LoadRatesAsync(CancellationToken ct) =>
        await db.ExchangeRates.AsNoTracking().ToDictionaryAsync(r => r.Currency, r => r.RateToTry, ct);

    private static List<CurrencyTotalDto> TotalsByCurrency(IEnumerable<Atm> atms, IReadOnlyDictionary<Currency, decimal> rates) =>
        atms.SelectMany(a => a.Cassettes)
            .SelectMany(c => c.Stocks.Select(s => (Cassette: c, Stock: s)))
            .Where(x => x.Stock.Count > 0)
            .GroupBy(x => x.Stock.Currency)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var amount = g.Sum(x => (long)x.Stock.Count * x.Stock.Denomination);
                return new CurrencyTotalDto(
                    g.Key,
                    g.Sum(x => x.Stock.Count),
                    g.Sum(x => x.Stock.Reserved),
                    g.Where(x => x.Cassette.Type == CassetteType.Recycling).Sum(x => x.Stock.Count - x.Stock.Reserved),
                    amount,
                    amount * rates.GetValueOrDefault(g.Key));
            })
            .ToList();
}
