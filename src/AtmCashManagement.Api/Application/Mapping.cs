using AtmCashManagement.Api.Domain;

namespace AtmCashManagement.Api.Application;

public static class Errors
{
    public static DomainException AtmNotFound(Guid atmId) =>
        new(ErrorCodes.AtmNotFound, $"ATM {atmId} was not found.", 404);

    public static DomainException QuoteNotFound(Guid quoteId) =>
        new(ErrorCodes.QuoteNotFound, $"Quote {quoteId} was not found for this ATM.", 404);

    public static DomainException Validation(string message) =>
        new(ErrorCodes.ValidationFailed, message, 400);

    public static string RequireUser(string? user)
    {
        if (string.IsNullOrWhiteSpace(user))
        {
            throw new DomainException(ErrorCodes.UserRequired,
                "The X-User header is required for this operation.", 400);
        }

        return user.Trim();
    }
}

public static class Mapping
{
    public static AtmDto ToDto(this Atm atm) => new(
        atm.Id, atm.Code, atm.Location, atm.State, atm.CreatedAt, atm.UpdatedAt,
        atm.Cassettes.OrderBy(c => c.Slot).Select(ToDto).ToList());

    public static CassetteDto ToDto(this Cassette cassette) => new(
        cassette.Id,
        cassette.Slot,
        cassette.Type,
        cassette.Currency,
        cassette.Denomination,
        cassette.Capacity,
        cassette.TotalCount(),
        cassette.TotalReserved(),
        cassette.AvailableCount(),
        cassette.Stocks
            .Where(s => s.Count > 0)
            .OrderBy(s => s.Currency).ThenBy(s => s.Denomination)
            .Select(s => new CassetteContentDto(s.Currency, s.Denomination, s.Count, (long)s.Count * s.Denomination))
            .ToList());

    public static QuoteDto ToDto(this WithdrawalQuote quote) => new(
        quote.Id,
        quote.AtmId,
        quote.Status,
        quote.Currency,
        quote.RequestedAmount,
        quote.Options.Any(o => o.Kind == QuoteOptionKind.Exact),
        quote.Options.Select(ToDto).ToList(),
        quote.CardLast4,
        quote.CreatedAt,
        quote.ExpiresAt,
        quote.ConfirmedOption,
        quote.ConfirmedAmount);

    public static QuoteOptionDto ToDto(this QuoteOption option) => new(
        option.Kind,
        option.Amount,
        option.Lines.Sum(l => l.Count),
        NotesOf(option),
        option.Lines
            .OrderByDescending(l => l.Denomination).ThenBy(l => l.Slot)
            .Select(l => new CassetteNoteLineDto(l.Slot, l.Denomination, l.Count))
            .ToList());

    public static IReadOnlyList<NoteLineDto> NotesOf(QuoteOption option) =>
        option.Lines
            .GroupBy(l => l.Denomination)
            .OrderByDescending(g => g.Key)
            .Select(g => new NoteLineDto(g.Key, g.Sum(l => l.Count)))
            .ToList();

    public static ReconciliationDto ToDto(this Reconciliation reconciliation, decimal approvalThresholdTry) => new(
        reconciliation.Id,
        reconciliation.AtmId,
        reconciliation.Status,
        reconciliation.TotalDifferenceTry,
        approvalThresholdTry,
        reconciliation.CreatedBy,
        reconciliation.CreatedAt,
        reconciliation.DecidedBy,
        reconciliation.DecidedAt,
        reconciliation.Lines);

    public static MovementDto ToDto(this CassetteMovement m) => new(
        m.Id, m.CassetteId, m.Currency, m.Denomination, m.Type, m.CountDelta, m.ReservedDelta,
        m.ReferenceType, m.ReferenceId, m.PerformedBy, m.CreatedAt);
}
