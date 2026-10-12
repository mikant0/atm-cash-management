using AtmCashManagement.Api.Application;
using AtmCashManagement.Api.Domain;
using Microsoft.AspNetCore.Mvc;

namespace AtmCashManagement.Api.Controllers;

/// <summary>Cash counts and their four-eyes approval. The acting user is taken from the X-User header.</summary>
[ApiController]
[Route("api/atms/{atmId:guid}/reconciliations")]
public sealed class ReconciliationsController(ReconciliationService reconciliations) : ControllerBase
{
    private const string UserHeader = "X-User";

    /// <summary>
    /// Records a physical count. Differences are booked as RECONCILIATION_ADJUSTMENT movements.
    /// If they are worth more than TRY 1,000 the result is AWAITING_APPROVAL and the ATM cannot
    /// return to service until a different user approves.
    /// </summary>
    [HttpPost]
    [ProducesResponseType<ReconciliationDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Create(Guid atmId, CreateReconciliationRequest request,
        [FromHeader(Name = UserHeader)] string? user, CancellationToken ct)
    {
        var reconciliation = await reconciliations.CreateAsync(atmId, request, user, ct);
        return CreatedAtAction(nameof(Get), new { atmId, reconciliationId = reconciliation.Id }, reconciliation);
    }

    [HttpGet]
    public Task<IReadOnlyList<ReconciliationDto>> List(Guid atmId, CancellationToken ct) =>
        reconciliations.ListAsync(atmId, ct);

    [HttpGet("{reconciliationId:guid}")]
    [ProducesResponseType<ReconciliationDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<ReconciliationDto> Get(Guid atmId, Guid reconciliationId, CancellationToken ct) =>
        reconciliations.GetAsync(atmId, reconciliationId, ct);

    /// <summary>Approves a reconciliation that is AWAITING_APPROVAL. Must be a different user than the one who counted.</summary>
    [HttpPost("{reconciliationId:guid}/approve")]
    [ProducesResponseType<ReconciliationDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<ReconciliationDto> Approve(Guid atmId, Guid reconciliationId,
        [FromHeader(Name = UserHeader)] string? user, CancellationToken ct) =>
        reconciliations.ApproveAsync(atmId, reconciliationId, user, ct);

    /// <summary>Rejects a reconciliation that is AWAITING_APPROVAL. The cash has to be counted again.</summary>
    [HttpPost("{reconciliationId:guid}/reject")]
    [ProducesResponseType<ReconciliationDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<ReconciliationDto> Reject(Guid atmId, Guid reconciliationId,
        [FromHeader(Name = UserHeader)] string? user, CancellationToken ct) =>
        reconciliations.RejectAsync(atmId, reconciliationId, user, ct);
}

/// <summary>Network-wide views for the operations team.</summary>
[ApiController]
[Route("api")]
public sealed class OperationsController(ReportService reports) : ControllerBase
{
    /// <summary>
    /// Operations report: note, reserved and available counts per ATM and cassette, totals per
    /// currency and the overall total in TRY. Pass atmId to limit it to one ATM.
    /// </summary>
    [HttpGet("reports/operations")]
    public Task<OperationsReportDto> GetOperationsReport([FromQuery] Guid? atmId, CancellationToken ct) =>
        reports.GetOperationsReportAsync(atmId, ct);

    /// <summary>Compares stored balances with balances rebuilt from the movement ledger, for all ATMs.</summary>
    [HttpGet("consistency-check")]
    public Task<ConsistencyReportDto> CheckConsistency(CancellationToken ct) =>
        reports.CheckConsistencyAsync(null, ct);

    [HttpGet("exchange-rates")]
    public Task<IReadOnlyList<ExchangeRateDto>> GetExchangeRates(CancellationToken ct) =>
        reports.GetExchangeRatesAsync(ct);

    /// <summary>Sets the TRY value of one unit of a currency. Used to value reconciliation differences.</summary>
    [HttpPut("exchange-rates/{currency}")]
    public Task<ExchangeRateDto> UpdateExchangeRate(Currency currency, UpdateExchangeRateRequest request,
        CancellationToken ct) =>
        reports.UpdateExchangeRateAsync(currency, request.RateToTry, ct);
}
