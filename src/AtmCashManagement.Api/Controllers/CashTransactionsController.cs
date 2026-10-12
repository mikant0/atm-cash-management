using AtmCashManagement.Api.Application;
using AtmCashManagement.Api.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace AtmCashManagement.Api.Controllers;

/// <summary>Customer transactions. Allowed only while the ATM is IN_SERVICE.</summary>
[ApiController]
[Route("api/atms/{atmId:guid}")]
public sealed class CashTransactionsController(WithdrawalService withdrawals, DepositService deposits) : ControllerBase
{
    public const string IdempotencyKeyHeader = "Idempotency-Key";
    public const string IdempotencyReplayedHeader = "Idempotency-Replayed";

    /// <summary>
    /// Step 1 of a withdrawal. Returns the exact note breakdown or, if the amount cannot be paid
    /// exactly, the LOWER and UPPER alternatives. The offered notes are reserved for 60 seconds.
    /// </summary>
    [HttpPost("withdrawals/quotes")]
    [ProducesResponseType<QuoteDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> CreateQuote(Guid atmId, CreateQuoteRequest request, CancellationToken ct)
    {
        var quote = await withdrawals.CreateQuoteAsync(atmId, request, ct);
        return CreatedAtAction(nameof(GetQuote), new { atmId, quoteId = quote.QuoteId }, quote);
    }

    [HttpGet("withdrawals/quotes/{quoteId:guid}")]
    [ProducesResponseType<QuoteDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<QuoteDto> GetQuote(Guid atmId, Guid quoteId, CancellationToken ct) =>
        withdrawals.GetQuoteAsync(atmId, quoteId, ct);

    /// <summary>
    /// Step 2 of a withdrawal: dispenses the cash. Repeating the request with the same Idempotency-Key
    /// returns the original result (with the header Idempotency-Replayed: true) and never debits twice.
    /// </summary>
    [HttpPost("withdrawals/quotes/{quoteId:guid}/confirm")]
    [ProducesResponseType<WithdrawalDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<WithdrawalDto> Confirm(Guid atmId, Guid quoteId,
        [FromHeader(Name = IdempotencyKeyHeader)] string? idempotencyKey,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] ConfirmQuoteRequest? request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128)
        {
            throw new DomainException(ErrorCodes.IdempotencyKeyRequired,
                $"The {IdempotencyKeyHeader} header is required (at most 128 characters).", 400);
        }

        var (result, replayed) = await withdrawals.ConfirmAsync(atmId, quoteId, request?.Option, idempotencyKey.Trim(), ct);
        if (replayed)
        {
            Response.Headers[IdempotencyReplayedHeader] = "true";
        }

        return result;
    }

    /// <summary>Cancels a quote and releases its reserved notes.</summary>
    [HttpPost("withdrawals/quotes/{quoteId:guid}/cancel")]
    [ProducesResponseType<QuoteDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<QuoteDto> Cancel(Guid atmId, Guid quoteId, CancellationToken ct) =>
        withdrawals.CancelAsync(atmId, quoteId, ct);

    /// <summary>
    /// Deposits notes. Each note goes to its matching recycling cassette, otherwise to the deposit
    /// cassette; notes that fit nowhere are returned. The response shows where every note went.
    /// </summary>
    [HttpPost("deposits")]
    [ProducesResponseType<DepositDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<DepositDto> Deposit(Guid atmId, DepositRequest request, CancellationToken ct) =>
        deposits.DepositAsync(atmId, request, ct);
}
