using System.Text.Json;
using AtmCashManagement.Api.Domain;
using AtmCashManagement.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AtmCashManagement.Api.Application;

/// <summary>Two-step withdrawal: a quote reserves notes, a confirmation dispenses them.</summary>
public sealed class WithdrawalService(
    AppDbContext db,
    CassetteLedger ledger,
    CardHasher cardHasher,
    BusinessCalendar calendar,
    TimeProvider clock,
    IOptions<CashOptions> options)
{
    private const string QuoteReference = "WithdrawalQuote";

    public async Task<QuoteDto> CreateQuoteAsync(Guid atmId, CreateQuoteRequest request, CancellationToken ct)
    {
        var currency = request.Currency!.Value;
        var now = clock.GetUtcNow();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var atm = await db.LockAtmAsync(atmId, ct) ?? throw Errors.AtmNotFound(atmId);

        AtmStateMachine.EnsureOperationAllowed(atm.State, AtmOperation.Withdraw);

        // Reservations of quotes that ran out of time are given back before availability is
        // calculated, so an expired quote never blocks a customer even if the background
        // sweeper has not reached it yet.
        await ExpireOverdueQuotesAsync(atm, now, ct);

        var cassettes = atm.GetRecyclingCassettes().Where(c => c.Currency == currency).ToList();
        if (cassettes.Count == 0)
        {
            throw new DomainException(ErrorCodes.CurrencyNotAvailable,
                $"This ATM does not dispense {currency}.", 422);
        }

        var cardHash = cardHasher.Hash(request.CardNumber);
        var remainingLimit = await GetRemainingDailyLimitAsync(cardHash, currency, now, ct);
        if (request.Amount > remainingLimit.Remaining)
        {
            throw new DomainException(ErrorCodes.DailyLimitExceeded,
                $"The requested amount exceeds the card's remaining daily {currency} limit of {remainingLimit.Remaining}.",
                422,
                new Dictionary<string, object?>
                {
                    ["currency"] = currency.ToString(),
                    ["dailyLimit"] = remainingLimit.Limit,
                    ["usedToday"] = remainingLimit.Limit - remainingLimit.Remaining,
                    ["remaining"] = remainingLimit.Remaining
                });
        }

        var available = cassettes
            .GroupBy(c => c.Denomination!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(c => c.AvailableCount()));

        var selection = NoteSelector.Select(available, request.Amount, maxUpperAmount: remainingLimit.Remaining);

        var offered = new List<(QuoteOptionKind Kind, NoteBreakdown Breakdown)>();
        if (selection.Exact is not null) offered.Add((QuoteOptionKind.Exact, selection.Exact));
        if (selection.Lower is not null) offered.Add((QuoteOptionKind.Lower, selection.Lower));
        if (selection.Upper is not null) offered.Add((QuoteOptionKind.Upper, selection.Upper));

        if (offered.Count == 0)
        {
            throw new DomainException(ErrorCodes.CannotDispense,
                $"The ATM cannot pay {request.Amount} {currency} or any nearby amount with the notes currently available.",
                422);
        }

        var quote = new WithdrawalQuote
        {
            Id = Guid.NewGuid(),
            AtmId = atm.Id,
            CardHash = cardHash,
            CardLast4 = CardHasher.Last4(request.CardNumber),
            Currency = currency,
            RequestedAmount = request.Amount,
            Status = QuoteStatus.Active,
            HeldAmount = offered.Max(o => o.Breakdown.Amount),
            BusinessDate = calendar.BusinessDateOf(now),
            CreatedAt = now,
            ExpiresAt = now.AddSeconds(options.Value.QuoteTtlSeconds),
            Options = offered.Select(o => ToOption(o.Kind, o.Breakdown, cassettes)).ToList()
        };

        // The customer confirms at most one option, so holding the larger need of each cassette
        // covers whichever one is chosen without locking away more notes than necessary.
        quote.Reservations = quote.Options
            .SelectMany(o => o.Lines)
            .GroupBy(l => (l.CassetteId, l.Denomination))
            .Select(g => new QuoteReservation
            {
                CassetteId = g.Key.CassetteId,
                Denomination = g.Key.Denomination,
                Count = g.Max(l => l.Count)
            })
            .ToList();

        foreach (var reservation in quote.Reservations)
        {
            var cassette = atm.Cassettes.Single(c => c.Id == reservation.CassetteId);
            ledger.Record(atm, cassette, currency, reservation.Denomination, MovementType.Reservation,
                0, reservation.Count, QuoteReference, quote.Id);
        }

        db.WithdrawalQuotes.Add(quote);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return quote.ToDto();
    }

    public async Task<QuoteDto> GetQuoteAsync(Guid atmId, Guid quoteId, CancellationToken ct)
    {
        var quote = await db.WithdrawalQuotes.AsNoTracking()
            .FirstOrDefaultAsync(q => q.Id == quoteId && q.AtmId == atmId, ct) ?? throw Errors.QuoteNotFound(quoteId);

        return quote.ToDto();
    }

    /// <returns>The withdrawal, and whether it is a stored result replayed for a repeated Idempotency-Key.</returns>
    public async Task<(WithdrawalDto Result, bool Replayed)> ConfirmAsync(Guid atmId, Guid quoteId,
        QuoteOptionKind? requestedOption, string idempotencyKey, CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // The ATM lock also serialises concurrent confirmations of the same quote, including
        // two requests that carry the same Idempotency-Key: the second one waits here and then
        // finds the record written by the first.
        var atm = await db.LockAtmAsync(atmId, ct) ?? throw Errors.AtmNotFound(atmId);

        var fingerprint = $"{quoteId:N}:{requestedOption?.ToString() ?? "default"}";
        var previous = await db.IdempotencyRecords.AsNoTracking().FirstOrDefaultAsync(r => r.Key == idempotencyKey, ct);
        if (previous is not null)
        {
            if (previous.RequestFingerprint != fingerprint)
            {
                throw new DomainException(ErrorCodes.IdempotencyKeyReused,
                    "This Idempotency-Key was already used for a different confirmation request.", 422);
            }

            var stored = JsonSerializer.Deserialize<WithdrawalDto>(previous.ResponseBody, JsonDefaults.Options)!;
            return (stored, true);
        }

        var quote = await db.WithdrawalQuotes.FirstOrDefaultAsync(q => q.Id == quoteId && q.AtmId == atmId, ct)
                    ?? throw Errors.QuoteNotFound(quoteId);

        EnsureActive(quote, now);
        AtmStateMachine.EnsureOperationAllowed(atm.State, AtmOperation.Withdraw);

        var option = ResolveOption(quote, requestedOption);

        foreach (var reservation in quote.Reservations)
        {
            var cassette = atm.Cassettes.Single(c => c.Id == reservation.CassetteId);
            var dispensed = option.Lines
                .Where(l => l.CassetteId == reservation.CassetteId && l.Denomination == reservation.Denomination)
                .Sum(l => l.Count);

            // The dispensed notes leave the cassette and stop being reserved in one movement.
            ledger.Record(atm, cassette, quote.Currency, reservation.Denomination, MovementType.Withdrawal,
                -dispensed, -dispensed, QuoteReference, quote.Id);

            // Notes that were held for the option the customer did not choose go back on offer.
            ledger.Record(atm, cassette, quote.Currency, reservation.Denomination, MovementType.ReservationRelease,
                0, -(reservation.Count - dispensed), QuoteReference, quote.Id);
        }

        quote.Status = QuoteStatus.Confirmed;
        quote.ConfirmedOption = option.Kind;
        quote.ConfirmedAmount = option.Amount;
        quote.CompletedAt = now;

        var result = new WithdrawalDto(quote.Id, quote.AtmId, quote.Status, option.Kind, quote.Currency,
            option.Amount, option.Lines.Sum(l => l.Count), Mapping.NotesOf(option), now);

        // Same transaction as the cash movement: either both exist or neither does.
        db.IdempotencyRecords.Add(new IdempotencyRecord
        {
            Key = idempotencyKey,
            QuoteId = quote.Id,
            RequestFingerprint = fingerprint,
            ResponseBody = JsonSerializer.Serialize(result, JsonDefaults.Options),
            CreatedAt = now
        });

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (result, false);
    }

    public async Task<QuoteDto> CancelAsync(Guid atmId, Guid quoteId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var atm = await db.LockAtmAsync(atmId, ct) ?? throw Errors.AtmNotFound(atmId);

        var quote = await db.WithdrawalQuotes.FirstOrDefaultAsync(q => q.Id == quoteId && q.AtmId == atmId, ct)
                    ?? throw Errors.QuoteNotFound(quoteId);

        if (quote.Status == QuoteStatus.Cancelled)
        {
            return quote.ToDto(); // Cancelling twice is harmless.
        }

        if (quote.Status == QuoteStatus.Confirmed)
        {
            throw new DomainException(ErrorCodes.QuoteAlreadyConfirmed,
                "The quote was already confirmed and the cash dispensed; it can no longer be cancelled.");
        }

        if (quote.Status == QuoteStatus.Active)
        {
            // An overdue quote that the customer cancels ends up released either way.
            Release(atm, quote, quote.IsExpiredAt(now) ? QuoteStatus.Expired : QuoteStatus.Cancelled, now);
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return quote.ToDto();
    }

    /// <summary>Used by the background sweeper. Returns the number of quotes it expired.</summary>
    public async Task<int> ExpireOverdueQuotesAsync(Guid atmId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var atm = await db.LockAtmAsync(atmId, ct);
        if (atm is null)
        {
            return 0;
        }

        var expired = await ExpireOverdueQuotesAsync(atm, clock.GetUtcNow(), ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return expired;
    }

    /// <summary>Cancels every active quote of an ATM. The caller holds the ATM lock and saves.</summary>
    public async Task CancelActiveQuotesAsync(Atm atm, DateTimeOffset now, CancellationToken ct)
    {
        var active = await db.WithdrawalQuotes
            .Where(q => q.AtmId == atm.Id && q.Status == QuoteStatus.Active)
            .ToListAsync(ct);

        foreach (var quote in active)
        {
            Release(atm, quote, quote.IsExpiredAt(now) ? QuoteStatus.Expired : QuoteStatus.Cancelled, now);
        }
    }

    private async Task<int> ExpireOverdueQuotesAsync(Atm atm, DateTimeOffset now, CancellationToken ct)
    {
        var overdue = await db.WithdrawalQuotes
            .Where(q => q.AtmId == atm.Id && q.Status == QuoteStatus.Active && q.ExpiresAt <= now)
            .ToListAsync(ct);

        foreach (var quote in overdue)
        {
            Release(atm, quote, QuoteStatus.Expired, now);
        }

        return overdue.Count;
    }

    private void Release(Atm atm, WithdrawalQuote quote, QuoteStatus finalStatus, DateTimeOffset now)
    {
        foreach (var reservation in quote.Reservations)
        {
            var cassette = atm.Cassettes.Single(c => c.Id == reservation.CassetteId);
            ledger.Record(atm, cassette, quote.Currency, reservation.Denomination, MovementType.ReservationRelease,
                0, -reservation.Count, QuoteReference, quote.Id);
        }

        quote.Status = finalStatus;
        quote.CompletedAt = now;
    }

    private static void EnsureActive(WithdrawalQuote quote, DateTimeOffset now)
    {
        switch (quote.Status)
        {
            case QuoteStatus.Confirmed:
                throw new DomainException(ErrorCodes.QuoteAlreadyConfirmed,
                    "The quote was already confirmed. Repeat the original request with its Idempotency-Key to get the result again.");
            case QuoteStatus.Cancelled:
                throw new DomainException(ErrorCodes.QuoteCancelled, "The quote was cancelled and cannot be confirmed.");
            case QuoteStatus.Expired:
                throw new DomainException(ErrorCodes.QuoteExpired, "The quote has expired. Request a new quote.");
        }

        // The status may still say ACTIVE if the sweeper has not run yet; time is what decides.
        if (quote.IsExpiredAt(now))
        {
            throw new DomainException(ErrorCodes.QuoteExpired, "The quote has expired. Request a new quote.");
        }
    }

    private static QuoteOption ResolveOption(WithdrawalQuote quote, QuoteOptionKind? requested)
    {
        if (requested is null)
        {
            if (quote.Options.Count == 1)
            {
                return quote.Options[0];
            }

            throw new DomainException(ErrorCodes.QuoteOptionRequired,
                "The quote offers more than one amount. Send the chosen option (LOWER or UPPER) in the request body.",
                400);
        }

        return quote.Options.FirstOrDefault(o => o.Kind == requested) ??
               throw new DomainException(ErrorCodes.QuoteOptionNotOffered,
                   $"Option {EnumText.Of(requested.Value)} was not offered in this quote. " +
                   $"Offered: {string.Join(", ", quote.Options.Select(o => EnumText.Of(o.Kind)))}.",
                   422);
    }

    private static QuoteOption ToOption(QuoteOptionKind kind, NoteBreakdown breakdown,
        IReadOnlyCollection<Cassette> cassettes)
    {
        var option = new QuoteOption { Kind = kind, Amount = breakdown.Amount };

        foreach (var (denomination, count) in breakdown.Notes.OrderByDescending(n => n.Key))
        {
            var sources = cassettes
                .Where(c => c.Denomination == denomination)
                .Select(c => new CassetteAllocator.Source(c.Id, c.Slot, c.AvailableCount(), c.Capacity))
                .ToList();

            foreach (var (cassetteId, taken) in CassetteAllocator.Dispense(sources, count))
            {
                option.Lines.Add(new QuoteOptionLine
                {
                    CassetteId = cassetteId,
                    Slot = sources.Single(s => s.CassetteId == cassetteId).Slot,
                    Denomination = denomination,
                    Count = taken
                });
            }
        }

        return option;
    }

    /// <summary>
    /// The limit is not kept in a counter; it is derived from the quotes themselves: confirmed
    /// withdrawals of the business day plus amounts held by quotes that are still valid.
    /// A transaction-scoped advisory lock on (card, currency) makes "check the limit, then
    /// reserve" atomic even when the same card is used at two ATMs at the same moment.
    /// </summary>
    private async Task<(int Limit, int Remaining)> GetRemainingDailyLimitAsync(string cardHash, Currency currency,
        DateTimeOffset now, CancellationToken ct)
    {
        var lockKey = $"{cardHash}:{currency}";
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", ct);

        var businessDate = calendar.BusinessDateOf(now);
        var sameCardToday = db.WithdrawalQuotes.Where(q =>
            q.CardHash == cardHash && q.Currency == currency && q.BusinessDate == businessDate);

        var confirmed = await sameCardToday
            .Where(q => q.Status == QuoteStatus.Confirmed)
            .SumAsync(q => q.ConfirmedAmount ?? 0, ct);

        var held = await sameCardToday
            .Where(q => q.Status == QuoteStatus.Active && q.ExpiresAt > now)
            .SumAsync(q => q.HeldAmount, ct);

        var limit = options.Value.DailyLimits.GetValueOrDefault(currency);
        return (limit, Math.Max(0, limit - confirmed - held));
    }
}
