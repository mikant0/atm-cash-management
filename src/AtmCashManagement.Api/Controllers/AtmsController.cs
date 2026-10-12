using AtmCashManagement.Api.Application;
using Microsoft.AspNetCore.Mvc;

namespace AtmCashManagement.Api.Controllers;

/// <summary>ATM network, cassette setup, cash loading and lifecycle.</summary>
[ApiController]
[Route("api/atms")]
public sealed class AtmsController(AtmService atms, ReportService reports) : ControllerBase
{
    /// <summary>Registers a new ATM. It starts in UNCONFIGURED.</summary>
    [HttpPost]
    [ProducesResponseType<AtmDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(CreateAtmRequest request, CancellationToken ct)
    {
        var atm = await atms.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { atmId = atm.Id }, atm);
    }

    [HttpGet]
    public Task<IReadOnlyList<AtmDto>> List(CancellationToken ct) => atms.ListAsync(ct);

    [HttpGet("{atmId:guid}")]
    [ProducesResponseType<AtmDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<AtmDto> Get(Guid atmId, CancellationToken ct) => atms.GetAsync(atmId, ct);

    /// <summary>
    /// Sets what each of the five cassettes holds. Allowed in UNCONFIGURED, CONFIGURED and MAINTENANCE,
    /// and only while all cassettes are empty.
    /// </summary>
    [HttpPut("{atmId:guid}/cassettes")]
    [ProducesResponseType<AtmDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public Task<AtmDto> ConfigureCassettes(Guid atmId, ConfigureCassettesRequest request, CancellationToken ct) =>
        atms.ConfigureCassettesAsync(atmId, request, ct);

    /// <summary>Records cash put into recycling cassettes by hand. Allowed in CONFIGURED and MAINTENANCE.</summary>
    [HttpPost("{atmId:guid}/cassettes/load")]
    [ProducesResponseType<AtmDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public Task<AtmDto> LoadCash(Guid atmId, LoadCashRequest request,
        [FromHeader(Name = "X-User")] string? user, CancellationToken ct) =>
        atms.LoadCashAsync(atmId, request, user, ct);

    /// <summary>Records that the given cassettes were emptied by hand. Allowed in MAINTENANCE.</summary>
    [HttpPost("{atmId:guid}/cassettes/unload")]
    [ProducesResponseType<AtmDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<AtmDto> UnloadCash(Guid atmId, UnloadCashRequest request,
        [FromHeader(Name = "X-User")] string? user, CancellationToken ct) =>
        atms.UnloadCashAsync(atmId, request, user, ct);

    /// <summary>Puts the ATM into service or takes it into maintenance.</summary>
    [HttpPost("{atmId:guid}/state")]
    [ProducesResponseType<AtmDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<AtmDto> ChangeState(Guid atmId, ChangeStateRequest request, CancellationToken ct) =>
        atms.ChangeStateAsync(atmId, request.TargetState!.Value, ct);

    /// <summary>Number of notes of each denomination in the ATM and its total balance.</summary>
    [HttpGet("{atmId:guid}/inventory")]
    [ProducesResponseType<InventoryDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<InventoryDto> GetInventory(Guid atmId, CancellationToken ct) => reports.GetInventoryAsync(atmId, ct);

    /// <summary>Latest cassette movements of the ATM, newest first.</summary>
    [HttpGet("{atmId:guid}/movements")]
    public Task<IReadOnlyList<MovementDto>> GetMovements(Guid atmId, [FromQuery] int? slot,
        [FromQuery] int limit = 100, CancellationToken ct = default) =>
        reports.GetMovementsAsync(atmId, slot, limit, ct);

    /// <summary>Compares the stored cassette balances of this ATM with the balances rebuilt from its movements.</summary>
    [HttpGet("{atmId:guid}/consistency-check")]
    public Task<ConsistencyReportDto> CheckConsistency(Guid atmId, CancellationToken ct) =>
        reports.CheckConsistencyAsync(atmId, ct);
}
