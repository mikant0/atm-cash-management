using System.Text.Json;

namespace AtmCashManagement.Api.Domain;

/// <summary>
/// A business rule violation. It is translated to a ProblemDetails response carrying
/// <see cref="Code"/> so that clients can react to the error without parsing text.
/// </summary>
public class DomainException : Exception
{
    public DomainException(string code, string message, int status = 409,
        IReadOnlyDictionary<string, object?>? details = null) : base(message)
    {
        Code = code;
        Status = status;
        Details = details;
    }

    public string Code { get; }
    public int Status { get; }
    public IReadOnlyDictionary<string, object?>? Details { get; }
}

public static class ErrorCodes
{
    public const string ValidationFailed = "validation.failed";
    public const string Conflict = "conflict";
    public const string Unexpected = "unexpected_error";

    public const string AtmNotFound = "atm.not_found";
    public const string AtmCodeExists = "atm.code_already_exists";
    public const string InvalidStateTransition = "atm.invalid_state_transition";
    public const string OperationNotAllowedInState = "atm.operation_not_allowed_in_state";
    public const string ReconciliationBlocksService = "atm.reconciliation_blocks_service";

    public const string InvalidCassetteLayout = "cassette.invalid_layout";
    public const string CassetteNotEmpty = "cassette.not_empty";
    public const string CassetteNotFound = "cassette.not_found";
    public const string CassetteCapacityExceeded = "cassette.capacity_exceeded";
    public const string DepositCassetteNotLoadable = "cassette.deposit_cassette_not_loadable";

    public const string CurrencyNotAvailable = "withdrawal.currency_not_available";
    public const string CannotDispense = "withdrawal.cannot_dispense";
    public const string DailyLimitExceeded = "withdrawal.daily_limit_exceeded";

    public const string QuoteNotFound = "quote.not_found";
    public const string QuoteExpired = "quote.expired";
    public const string QuoteCancelled = "quote.cancelled";
    public const string QuoteAlreadyConfirmed = "quote.already_confirmed";
    public const string QuoteOptionRequired = "quote.option_required";
    public const string QuoteOptionNotOffered = "quote.option_not_offered";

    public const string IdempotencyKeyRequired = "idempotency.key_required";
    public const string IdempotencyKeyReused = "idempotency.key_reused_with_different_request";

    public const string UserRequired = "user.header_required";

    public const string ReconciliationNotFound = "reconciliation.not_found";
    public const string ReconciliationPending = "reconciliation.pending_approval_exists";
    public const string ReconciliationNotAwaitingApproval = "reconciliation.not_awaiting_approval";
    public const string FourEyesViolation = "reconciliation.four_eyes_violation";
    public const string ReconciliationIncomplete = "reconciliation.incomplete_count";

    public const string ExchangeRateMissing = "exchange_rate.missing";
}

public static class EnumText
{
    /// <summary>Formats an enum the same way the API serialises it, e.g. InService -> IN_SERVICE.</summary>
    public static string Of<TEnum>(TEnum value) where TEnum : struct, Enum =>
        JsonNamingPolicy.SnakeCaseUpper.ConvertName(value.ToString());
}
