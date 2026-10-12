using System.ComponentModel.DataAnnotations;
using AtmCashManagement.Api.Domain;

namespace AtmCashManagement.Api.Application;

// ---------- ATM setup ----------

public sealed class CreateAtmRequest
{
    /// <summary>Unique business identifier of the ATM, e.g. "IST-0042".</summary>
    [Required, StringLength(32, MinimumLength = 1)]
    public string Code { get; init; } = "";

    [StringLength(200)]
    public string? Location { get; init; }
}

public sealed class CassetteConfigItem
{
    [Range(1, Banknotes.CassettesPerAtm)]
    public int Slot { get; init; }

    [Required]
    public CassetteType? Type { get; init; }

    /// <summary>Required for RECYCLING, must be omitted for DEPOSIT.</summary>
    public Currency? Currency { get; init; }

    /// <summary>Required for RECYCLING (20, 50, 100 or 500), must be omitted for DEPOSIT.</summary>
    public int? Denomination { get; init; }
}

public sealed class ConfigureCassettesRequest
{
    /// <summary>All five cassettes: four RECYCLING and one DEPOSIT.</summary>
    [Required]
    public List<CassetteConfigItem> Cassettes { get; init; } = [];
}

public sealed class LoadItem
{
    [Range(1, Banknotes.CassettesPerAtm)]
    public int Slot { get; init; }

    [Range(1, Banknotes.CassetteCapacity)]
    public int Count { get; init; }
}

public sealed class LoadCashRequest
{
    [Required, MinLength(1)]
    public List<LoadItem> Items { get; init; } = [];
}

public sealed class UnloadCashRequest
{
    /// <summary>Slots to empty completely.</summary>
    [Required, MinLength(1)]
    public List<int> Slots { get; init; } = [];
}

public sealed class ChangeStateRequest
{
    /// <summary>IN_SERVICE or MAINTENANCE.</summary>
    [Required]
    public AtmState? TargetState { get; init; }
}

public sealed record CassetteContentDto(Currency Currency, int Denomination, int NoteCount, long Amount);

/// <summary>
/// AvailableCount is the number of notes that can still be offered to customers
/// (note count minus reserved). It is always 0 for the deposit cassette, which never dispenses.
/// </summary>
public sealed record CassetteDto(
    Guid Id,
    int Slot,
    CassetteType Type,
    Currency? Currency,
    int? Denomination,
    int Capacity,
    int NoteCount,
    int ReservedCount,
    int AvailableCount,
    IReadOnlyList<CassetteContentDto> Contents);

public sealed record AtmDto(
    Guid Id,
    string Code,
    string? Location,
    AtmState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<CassetteDto> Cassettes);

// ---------- Inventory and reporting ----------

public sealed record DenominationCountDto(
    Currency Currency,
    int Denomination,
    int NoteCount,
    int ReservedCount,
    int AvailableCount,
    long Amount);

public sealed record CurrencyTotalDto(
    Currency Currency,
    int NoteCount,
    int ReservedCount,
    int AvailableCount,
    long Amount,
    decimal ValueTry);

public sealed record InventoryDto(
    Guid AtmId,
    string Code,
    AtmState State,
    IReadOnlyList<DenominationCountDto> Notes,
    IReadOnlyList<CurrencyTotalDto> TotalsByCurrency,
    decimal TotalValueTry);

public sealed record AtmReportDto(
    Guid AtmId,
    string Code,
    string? Location,
    AtmState State,
    IReadOnlyList<CassetteDto> Cassettes,
    IReadOnlyList<CurrencyTotalDto> TotalsByCurrency,
    decimal TotalValueTry);

public sealed record OperationsReportDto(
    DateTimeOffset GeneratedAt,
    IReadOnlyDictionary<Currency, decimal> RatesToTry,
    IReadOnlyList<AtmReportDto> Atms,
    IReadOnlyList<CurrencyTotalDto> TotalsByCurrency,
    decimal TotalValueTry);

public sealed record MovementDto(
    long Id,
    Guid CassetteId,
    Currency Currency,
    int Denomination,
    MovementType Type,
    int CountDelta,
    int ReservedDelta,
    string? ReferenceType,
    Guid? ReferenceId,
    string? PerformedBy,
    DateTimeOffset CreatedAt);

public sealed record LedgerDifferenceDto(
    Guid AtmId,
    string AtmCode,
    Guid CassetteId,
    int Slot,
    Currency Currency,
    int Denomination,
    int StoredCount,
    int LedgerCount,
    int StoredReserved,
    int LedgerReserved);

public sealed record ConsistencyReportDto(
    DateTimeOffset CheckedAt,
    bool Consistent,
    int CheckedBalances,
    IReadOnlyList<LedgerDifferenceDto> Differences);

// ---------- Withdrawal ----------

public sealed class CreateQuoteRequest
{
    /// <summary>Card number, digits only. Used for the daily limit; only a keyed hash is stored.</summary>
    [Required, RegularExpression(@"^\d{12,19}$", ErrorMessage = "The card number must be 12 to 19 digits.")]
    public string CardNumber { get; init; } = "";

    [Required]
    public Currency? Currency { get; init; }

    [Range(1, 1_000_000)]
    public int Amount { get; init; }
}

public sealed class ConfirmQuoteRequest
{
    /// <summary>
    /// Which offered breakdown to dispense. May be omitted when the quote has a single option.
    /// </summary>
    public QuoteOptionKind? Option { get; init; }
}

public sealed record NoteLineDto(int Denomination, int Count);

public sealed record CassetteNoteLineDto(int Slot, int Denomination, int Count);

public sealed record QuoteOptionDto(
    QuoteOptionKind Option,
    int Amount,
    int NoteCount,
    IReadOnlyList<NoteLineDto> Notes,
    IReadOnlyList<CassetteNoteLineDto> Cassettes);

public sealed record QuoteDto(
    Guid QuoteId,
    Guid AtmId,
    QuoteStatus Status,
    Currency Currency,
    int RequestedAmount,
    bool ExactMatch,
    IReadOnlyList<QuoteOptionDto> Options,
    string CardLast4,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    QuoteOptionKind? ConfirmedOption,
    int? ConfirmedAmount);

public sealed record WithdrawalDto(
    Guid QuoteId,
    Guid AtmId,
    QuoteStatus Status,
    QuoteOptionKind Option,
    Currency Currency,
    int DispensedAmount,
    int NoteCount,
    IReadOnlyList<NoteLineDto> Notes,
    DateTimeOffset ConfirmedAt);

// ---------- Deposit ----------

public sealed class DepositNoteItem
{
    [Required]
    public Currency? Currency { get; init; }

    public int Denomination { get; init; }

    [Range(1, 10_000)]
    public int Count { get; init; }
}

public sealed class DepositRequest
{
    /// <summary>Notes the machine recognised.</summary>
    public List<DepositNoteItem> Notes { get; init; } = [];

    /// <summary>Notes the machine could not recognise. They are handed back and not part of the transaction.</summary>
    [Range(0, 10_000)]
    public int UnrecognisedCount { get; init; }
}

public sealed record DepositAllocationDto(
    Currency Currency,
    int Denomination,
    int Count,
    int Slot,
    CassetteType CassetteType);

public sealed record ReturnedNotesDto(Currency Currency, int Denomination, int Count, string Reason);

public sealed record DepositedTotalDto(Currency Currency, int NoteCount, long Amount);

public sealed record DepositDto(
    Guid DepositId,
    Guid AtmId,
    IReadOnlyList<DepositAllocationDto> Allocations,
    IReadOnlyList<ReturnedNotesDto> ReturnedNotes,
    int UnrecognisedReturnedCount,
    IReadOnlyList<DepositedTotalDto> AcceptedTotals,
    DateTimeOffset CreatedAt);

// ---------- Reconciliation ----------

public sealed class DepositCountItem
{
    [Required]
    public Currency? Currency { get; init; }

    public int Denomination { get; init; }

    [Range(0, Banknotes.CassetteCapacity)]
    public int Count { get; init; }
}

public sealed class CassetteCountItem
{
    [Range(1, Banknotes.CassettesPerAtm)]
    public int Slot { get; init; }

    /// <summary>Counted notes in a RECYCLING cassette.</summary>
    [Range(0, Banknotes.CassetteCapacity)]
    public int? CountedNotes { get; init; }

    /// <summary>
    /// Counted notes in the DEPOSIT cassette, per currency and denomination.
    /// Anything not listed is taken as counted zero; an empty list means the cassette is empty.
    /// </summary>
    public List<DepositCountItem>? DepositCounts { get; init; }
}

public sealed class CreateReconciliationRequest
{
    /// <summary>One entry for each of the five cassettes.</summary>
    [Required]
    public List<CassetteCountItem> Cassettes { get; init; } = [];
}

public sealed record ReconciliationDto(
    Guid Id,
    Guid AtmId,
    ReconciliationStatus Status,
    decimal TotalDifferenceTry,
    decimal ApprovalThresholdTry,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    string? DecidedBy,
    DateTimeOffset? DecidedAt,
    IReadOnlyList<ReconciliationLine> Lines);

// ---------- Exchange rates ----------

public sealed class UpdateExchangeRateRequest
{
    /// <summary>Value of one unit of the currency in TRY.</summary>
    [Range(0.000001, 1_000_000)]
    public decimal RateToTry { get; init; }
}

public sealed record ExchangeRateDto(Currency Currency, decimal RateToTry, DateTimeOffset UpdatedAt);
