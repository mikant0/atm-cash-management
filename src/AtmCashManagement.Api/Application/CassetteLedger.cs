using System.Text.Json;
using AtmCashManagement.Api.Domain;
using AtmCashManagement.Api.Infrastructure;
using Microsoft.Extensions.Options;

namespace AtmCashManagement.Api.Application;

/// <summary>
/// The only place that changes a cassette balance. Every change updates the stored balance and
/// appends the matching movement in the same unit of work, which is what keeps the balance
/// reproducible from the ledger. The caller must hold the ATM lock.
/// </summary>
public sealed class CassetteLedger(AppDbContext db, TimeProvider clock, IOptions<CashOptions> options)
{
    public void Record(Atm atm, Cassette cassette, Currency currency, int denomination, MovementType type,
        int countDelta, int reservedDelta, string? referenceType = null, Guid? referenceId = null,
        string? performedBy = null)
    {
        if (countDelta == 0 && reservedDelta == 0)
        {
            return;
        }

        var stock = cassette.GetStock(currency, denomination);
        if (stock is null)
        {
            stock = new CassetteStock
            {
                Id = Guid.NewGuid(),
                CassetteId = cassette.Id,
                Currency = currency,
                Denomination = denomination
            };
            cassette.Stocks.Add(stock);
            db.CassetteStocks.Add(stock);
        }

        var availableBefore = cassette.AvailableCount();

        stock.Count += countDelta;
        stock.Reserved += reservedDelta;

        if (stock.Count < 0 || stock.Reserved < 0 || stock.Reserved > stock.Count ||
            cassette.TotalCount() > cassette.Capacity)
        {
            // Callers validate before they get here, so this is a programming error, not a user error.
            throw new InvalidOperationException(
                $"Movement {type} would leave cassette {cassette.Slot} of ATM {atm.Code} in an invalid state " +
                $"(count {stock.Count}, reserved {stock.Reserved}, capacity {cassette.Capacity}).");
        }

        var now = clock.GetUtcNow();
        db.CassetteMovements.Add(new CassetteMovement(atm.Id, cassette.Id, currency, denomination, type,
            countDelta, reservedDelta, referenceType, referenceId, performedBy, now));

        RaiseLowCashAlert(atm, cassette, type, availableBefore, now);
    }

    /// <summary>
    /// Adds the alert to the outbox when the available count crosses the threshold downwards.
    /// It is part of the caller's transaction, so the alert exists if and only if the change does.
    /// Raised on the crossing only, not for every later withdrawal from an already low cassette.
    /// </summary>
    private void RaiseLowCashAlert(Atm atm, Cassette cassette, MovementType type, int availableBefore,
        DateTimeOffset now)
    {
        if (cassette.Type != CassetteType.Recycling || type == MovementType.Unload)
        {
            return;
        }

        var threshold = (int)Math.Ceiling(cassette.Capacity * options.Value.LowCashThresholdRatio);
        var availableAfter = cassette.AvailableCount();
        if (availableBefore < threshold || availableAfter >= threshold)
        {
            return;
        }

        var payload = new
        {
            atmId = atm.Id,
            atmCode = atm.Code,
            cassetteId = cassette.Id,
            slot = cassette.Slot,
            currency = cassette.Currency,
            denomination = cassette.Denomination,
            availableCount = availableAfter,
            capacity = cassette.Capacity,
            threshold,
            occurredAt = now
        };

        db.OutboxMessages.Add(new OutboxMessage
        {
            Type = OutboxMessage.LowCashAlert,
            Payload = JsonSerializer.Serialize(payload, JsonDefaults.Options),
            CreatedAt = now
        });
    }
}
