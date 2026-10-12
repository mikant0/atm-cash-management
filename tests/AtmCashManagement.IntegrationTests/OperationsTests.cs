using System.Net;
using System.Net.Http.Json;
using AtmCashManagement.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using static AtmCashManagement.IntegrationTests.ApiClientExtensions;

namespace AtmCashManagement.IntegrationTests;

[Collection(ApiCollection.Name)]
public class OperationsTests(ApiFixture api)
{
    private readonly HttpClient _client = api.CreateClient();

    [Fact(DisplayName = "End to end: create, configure, load, serve, withdraw, deposit, maintain, reconcile, report")]
    public async Task End_to_end_scenario()
    {
        var created = await _client.PostAsJsonAsync("/api/atms", new { code = $"E2E-{Guid.NewGuid():N}"[..16], location = "Levent" })
            .ExpectAsync(HttpStatusCode.Created);
        var atmId = (Guid)created["id"]!;
        Assert.Equal("UNCONFIGURED", (string?)created["state"]);

        var configured = await _client.PutAsJsonAsync($"/api/atms/{atmId}/cassettes", new { cassettes = StandardLayout() })
            .ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("CONFIGURED", (string?)configured["state"]);

        var loaded = await _client.PostAsJsonAsync($"/api/atms/{atmId}/cassettes/load", new
        {
            items = new[] { new { slot = 1, count = 50 }, new { slot = 2, count = 50 }, new { slot = 3, count = 50 }, new { slot = 4, count = 20 } }
        }).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("LOADED", (string?)loaded["state"]);

        var inService = await _client.SetStateAsync(atmId, "IN_SERVICE").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("IN_SERVICE", (string?)inService["state"]);

        // Withdraw 270 = 100x2 + 50 + 20.
        var quote = await _client.QuoteAsync(atmId, NewCardNumber(), "TRY", 270).ExpectAsync(HttpStatusCode.Created);
        Assert.True((bool)quote["exactMatch"]!);
        Assert.Equal(4, (int)quote["options"]![0]!["noteCount"]!);
        var withdrawal = await _client.ConfirmAsync(atmId, (Guid)quote["quoteId"]!, Guid.NewGuid().ToString())
            .ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(270, (int)withdrawal["dispensedAmount"]!);

        // Deposit: TRY 100 has a recycling cassette, EUR 50 does not.
        var deposit = await _client.PostAsJsonAsync($"/api/atms/{atmId}/deposits", new
        {
            notes = new object[]
            {
                new { currency = "TRY", denomination = 100, count = 3 },
                new { currency = "EUR", denomination = 50, count = 2 }
            },
            unrecognisedCount = 1
        }).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(["TRY 100 x3 -> slot 1 RECYCLING", "EUR 50 x2 -> slot 5 DEPOSIT"],
            deposit["allocations"]!.AsArray().Select(a =>
                $"{a!["currency"]} {a["denomination"]} x{a["count"]} -> slot {a["slot"]} {a["cassetteType"]}"));
        Assert.Equal(1, (int)deposit["unrecognisedReturnedCount"]!);

        await _client.SetStateAsync(atmId, "MAINTENANCE").ExpectAsync(HttpStatusCode.OK);

        // System: slot1 51, slot2 49, slot3 49, slot4 20, deposit EUR 50 x2. One TRY 20 is missing.
        var reconciliation = await _client.PostAsUserAsync($"/api/atms/{atmId}/reconciliations", "alice",
                CountRequest(51, 49, 48, 20, new { currency = "EUR", denomination = 50, count = 2 }))
            .ExpectAsync(HttpStatusCode.Created);
        Assert.Equal("COMPLETED", (string?)reconciliation["status"]);
        Assert.Equal(20m, (decimal)reconciliation["totalDifferenceTry"]!);

        await _client.SetStateAsync(atmId, "IN_SERVICE").ExpectAsync(HttpStatusCode.OK);

        // A12: notes per denomination and total balance.
        var inventory = await _client.GetAsync($"/api/atms/{atmId}/inventory").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(["EUR 50 x2", "TRY 100 x51", "TRY 20 x48", "TRY 50 x49", "USD 100 x20"],
            inventory["notes"]!.AsArray().Select(n => $"{n!["currency"]} {n["denomination"]} x{n["noteCount"]}").Order());
        var tryTotal = inventory["totalsByCurrency"]!.AsArray().Single(t => (string?)t!["currency"] == "TRY")!;
        Assert.Equal(51 * 100 + 49 * 50 + 48 * 20, (int)tryTotal["amount"]!);

        // B10: operations report.
        var report = await _client.GetAsync($"/api/reports/operations?atmId={atmId}").ExpectAsync(HttpStatusCode.OK);
        var atmReport = Assert.Single(report["atms"]!.AsArray())!;
        Assert.Equal(5, atmReport["cassettes"]!.AsArray().Count);
        var usdRate = (decimal)report["ratesToTry"]!["USD"]!;
        var eurRate = (decimal)report["ratesToTry"]!["EUR"]!;
        Assert.Equal(8510m + 2000m * usdRate + 100m * eurRate, (decimal)report["totalValueTry"]!);
        Assert.Contains(atmReport["cassettes"]!.AsArray(),
            c => (int)c!["slot"]! == 1 && (int)c["noteCount"]! == 51 && (int)c["reservedCount"]! == 0 && (int)c["availableCount"]! == 51);

        await _client.AssertLedgerConsistentAsync(atmId);
    }

    [Fact(DisplayName = "B2: operations in the wrong state and invalid transitions are rejected with meaningful errors")]
    public async Task Lifecycle_rules_are_enforced()
    {
        var atmId = await _client.CreateAtmAsync();
        var card = NewCardNumber();
        var load = new { items = new[] { new { slot = 1, count = 10 } } };

        // UNCONFIGURED
        await _client.QuoteAsync(atmId, card, "TRY", 100)
            .ExpectProblemAsync(HttpStatusCode.Conflict, ErrorCodes.OperationNotAllowedInState);
        await _client.PostAsJsonAsync($"/api/atms/{atmId}/cassettes/load", load)
            .ExpectProblemAsync(HttpStatusCode.Conflict, ErrorCodes.OperationNotAllowedInState);
        var transition = await _client.SetStateAsync(atmId, "IN_SERVICE")
            .ExpectProblemAsync(HttpStatusCode.Conflict, ErrorCodes.InvalidStateTransition);
        Assert.Equal("UNCONFIGURED", (string?)transition["currentState"]);

        // Part A.6: at least one and at most two foreign currency cassettes.
        object[] noForeign =
        [
            Recycling(1, "TRY", 100), Recycling(2, "TRY", 50), Recycling(3, "TRY", 20), Recycling(4, "TRY", 500),
            DepositCassette(5)
        ];
        await _client.PutAsJsonAsync($"/api/atms/{atmId}/cassettes", new { cassettes = noForeign })
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, ErrorCodes.InvalidCassetteLayout);

        // CONFIGURED: still cannot go into service without cash (Part A.7).
        await _client.PutAsJsonAsync($"/api/atms/{atmId}/cassettes", new { cassettes = StandardLayout() }).ExpectAsync(HttpStatusCode.OK);
        await _client.SetStateAsync(atmId, "IN_SERVICE")
            .ExpectProblemAsync(HttpStatusCode.Conflict, ErrorCodes.InvalidStateTransition);
        await _client.PostAsJsonAsync($"/api/atms/{atmId}/cassettes/load", new { items = new[] { new { slot = 5, count = 1 } } })
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, ErrorCodes.DepositCassetteNotLoadable);
        await _client.PostAsJsonAsync($"/api/atms/{atmId}/cassettes/load", new { items = new[] { new { slot = 1, count = 60 }, new { slot = 1, count = 60 } } })
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, ErrorCodes.CassetteCapacityExceeded);

        // LOADED
        await _client.PostAsJsonAsync($"/api/atms/{atmId}/cassettes/load", load).ExpectAsync(HttpStatusCode.OK);
        await _client.QuoteAsync(atmId, card, "TRY", 100)
            .ExpectProblemAsync(HttpStatusCode.Conflict, ErrorCodes.OperationNotAllowedInState);

        // IN_SERVICE
        await _client.SetStateAsync(atmId, "IN_SERVICE").ExpectAsync(HttpStatusCode.OK);
        await _client.PutAsJsonAsync($"/api/atms/{atmId}/cassettes", new { cassettes = StandardLayout() })
            .ExpectProblemAsync(HttpStatusCode.Conflict, ErrorCodes.OperationNotAllowedInState);
        await _client.PostAsJsonAsync($"/api/atms/{atmId}/cassettes/load", load)
            .ExpectProblemAsync(HttpStatusCode.Conflict, ErrorCodes.OperationNotAllowedInState);
        await _client.PostAsUserAsync($"/api/atms/{atmId}/reconciliations", "alice", CountRequest(10, 0, 0, 0))
            .ExpectProblemAsync(HttpStatusCode.Conflict, ErrorCodes.OperationNotAllowedInState);
        await _client.SetStateAsync(atmId, "IN_SERVICE")
            .ExpectProblemAsync(HttpStatusCode.Conflict, ErrorCodes.InvalidStateTransition);
        await _client.QuoteAsync(atmId, card, "EUR", 100)
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, ErrorCodes.CurrencyNotAvailable);

        // Going into maintenance cancels open quotes and frees their notes.
        var quote = await _client.QuoteAsync(atmId, card, "TRY", 100).ExpectAsync(HttpStatusCode.Created);
        await _client.SetStateAsync(atmId, "MAINTENANCE").ExpectAsync(HttpStatusCode.OK);
        var cancelled = await _client.GetAsync($"/api/atms/{atmId}/withdrawals/quotes/{quote["quoteId"]}").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("CANCELLED", (string?)cancelled["status"]);
        Assert.Equal(0, (int)(await _client.CassetteAsync(atmId, 1))["reservedCount"]!);

        // MAINTENANCE
        await _client.QuoteAsync(atmId, card, "TRY", 100)
            .ExpectProblemAsync(HttpStatusCode.Conflict, ErrorCodes.OperationNotAllowedInState);
        await _client.PostAsJsonAsync($"/api/atms/{atmId}/deposits", new { notes = new[] { new { currency = "TRY", denomination = 100, count = 1 } } })
            .ExpectProblemAsync(HttpStatusCode.Conflict, ErrorCodes.OperationNotAllowedInState);
        await _client.PutAsJsonAsync($"/api/atms/{atmId}/cassettes", new { cassettes = StandardLayout() })
            .ExpectProblemAsync(HttpStatusCode.Conflict, ErrorCodes.CassetteNotEmpty);
        await _client.PostAsJsonAsync($"/api/atms/{atmId}/cassettes/unload", new { slots = new[] { 1, 2, 3, 4, 5 } }).ExpectAsync(HttpStatusCode.OK);
        object[] newLayout =
        [
            Recycling(1, "TRY", 500), Recycling(2, "TRY", 100), Recycling(3, "EUR", 50), Recycling(4, "USD", 100),
            DepositCassette(5)
        ];
        var reconfigured = await _client.PutAsJsonAsync($"/api/atms/{atmId}/cassettes", new { cassettes = newLayout }).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("MAINTENANCE", (string?)reconfigured["state"]);
        await _client.PostAsJsonAsync($"/api/atms/{atmId}/cassettes/load", load).ExpectAsync(HttpStatusCode.OK);
        await _client.AssertLedgerConsistentAsync(atmId);

        // Generic errors use the same model.
        await _client.GetAsync($"/api/atms/{Guid.NewGuid()}").ExpectProblemAsync(HttpStatusCode.NotFound, ErrorCodes.AtmNotFound);
        var invalid = await _client.PostAsJsonAsync($"/api/atms/{atmId}/withdrawals/quotes", new { cardNumber = "12", currency = "GBP", amount = 0 })
            .ExpectProblemAsync(HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);
        Assert.NotNull(invalid["errors"]);
    }

    [Fact(DisplayName = "B7: deposits overflow to the deposit cassette and return what does not fit")]
    public async Task Deposit_routes_overflows_and_returns_notes()
    {
        var atmId = await _client.CreateAtmInServiceAsync(StandardLayout(), (1, 98));

        var first = await _client.PostAsJsonAsync($"/api/atms/{atmId}/deposits", new
        {
            notes = new object[]
            {
                new { currency = "TRY", denomination = 100, count = 5 },
                new { currency = "EUR", denomination = 50, count = 3 }
            },
            unrecognisedCount = 2
        }).ExpectAsync(HttpStatusCode.OK);

        // Two notes fill the recycling cassette, three overflow; EUR has no recycling cassette.
        Assert.Equal(["TRY 100 x2 -> 1", "TRY 100 x3 -> 5", "EUR 50 x3 -> 5"],
            first["allocations"]!.AsArray().Select(a => $"{a!["currency"]} {a["denomination"]} x{a["count"]} -> {a["slot"]}"));
        Assert.Empty(first["returnedNotes"]!.AsArray());
        Assert.Equal(2, (int)first["unrecognisedReturnedCount"]!);

        // The deposit cassette has 94 places left.
        var second = await _client.PostAsJsonAsync($"/api/atms/{atmId}/deposits", new
        {
            notes = new[] { new { currency = "EUR", denomination = 20, count = 100 } }
        }).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(94, (int)Assert.Single(second["allocations"]!.AsArray())!["count"]!);
        var returned = Assert.Single(second["returnedNotes"]!.AsArray())!;
        Assert.Equal((6, "CASSETTES_FULL"), ((int)returned["count"]!, (string?)returned["reason"]));

        // Everything is full now.
        var third = await _client.PostAsJsonAsync($"/api/atms/{atmId}/deposits", new
        {
            notes = new[] { new { currency = "TRY", denomination = 100, count = 1 } }
        }).ExpectAsync(HttpStatusCode.OK);
        Assert.Empty(third["allocations"]!.AsArray());
        Assert.Equal(1, (int)Assert.Single(third["returnedNotes"]!.AsArray())!["count"]!);

        Assert.Equal(100, (int)(await _client.CassetteAsync(atmId, 1))["noteCount"]!);
        var depositCassette = await _client.CassetteAsync(atmId, 5);
        Assert.Equal(100, (int)depositCassette["noteCount"]!);
        Assert.Equal(0, (int)depositCassette["availableCount"]!);

        await _client.PostAsJsonAsync($"/api/atms/{atmId}/deposits", new { notes = new[] { new { currency = "TRY", denomination = 200, count = 1 } } })
            .ExpectProblemAsync(HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);

        // Recycled notes can be paid out again, notes in the deposit cassette cannot.
        var quote = await _client.QuoteAsync(atmId, NewCardNumber(), "TRY", 10_000).ExpectAsync(HttpStatusCode.Created);
        Assert.True((bool)quote["exactMatch"]!);
        await _client.QuoteAsync(atmId, NewCardNumber(), "EUR", 50)
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, ErrorCodes.CurrencyNotAvailable);
        await _client.AssertLedgerConsistentAsync(atmId);
    }

    [Fact(DisplayName = "B9: a large reconciliation difference needs approval by a different user")]
    public async Task Reconciliation_requires_four_eyes_approval_above_the_threshold()
    {
        var atmId = await _client.CreateAtmInServiceAsync(StandardLayout(), (1, 50), (2, 50), (3, 50), (4, 10));
        await _client.SetStateAsync(atmId, "MAINTENANCE").ExpectAsync(HttpStatusCode.OK);
        var url = $"/api/atms/{atmId}/reconciliations";

        await _client.PostAsJsonAsync(url, CountRequest(50, 50, 50, 10))
            .ExpectProblemAsync(HttpStatusCode.BadRequest, ErrorCodes.UserRequired);
        await _client.PostAsUserAsync(url, "alice", new { cassettes = new[] { new { slot = 1, countedNotes = 50 } } })
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, ErrorCodes.ReconciliationIncomplete);

        // One TRY 100 note short: TRY 100, below the threshold.
        var small = await _client.PostAsUserAsync(url, "alice", CountRequest(49, 50, 50, 10)).ExpectAsync(HttpStatusCode.Created);
        Assert.Equal("COMPLETED", (string?)small["status"]);
        Assert.Equal(49, (int)(await _client.CassetteAsync(atmId, 1))["noteCount"]!);
        await _client.SetStateAsync(atmId, "IN_SERVICE").ExpectAsync(HttpStatusCode.OK);
        await _client.SetStateAsync(atmId, "MAINTENANCE").ExpectAsync(HttpStatusCode.OK);

        // Three TRY 100 short and one USD 100 over: 300 + 100 x rate, well above TRY 1,000.
        var usdRate = (decimal)(await _client.GetAsync("/api/exchange-rates").ExpectAsync(HttpStatusCode.OK))
            .AsArray().Single(r => (string?)r!["currency"] == "USD")!["rateToTry"]!;
        var large = await _client.PostAsUserAsync(url, "alice", CountRequest(46, 50, 50, 11)).ExpectAsync(HttpStatusCode.Created);
        var reconciliationId = (Guid)large["id"]!;
        Assert.Equal("AWAITING_APPROVAL", (string?)large["status"]);
        Assert.Equal(300m + 100m * usdRate, (decimal)large["totalDifferenceTry"]!);

        // The adjustment is booked, but the ATM is blocked.
        Assert.Equal(46, (int)(await _client.CassetteAsync(atmId, 1))["noteCount"]!);
        Assert.Equal(11, (int)(await _client.CassetteAsync(atmId, 4))["noteCount"]!);
        var blocked = await _client.SetStateAsync(atmId, "IN_SERVICE")
            .ExpectProblemAsync(HttpStatusCode.Conflict, ErrorCodes.ReconciliationBlocksService);
        Assert.Equal(reconciliationId, (Guid)blocked["reconciliationId"]!);
        await _client.PostAsUserAsync(url, "alice", CountRequest(46, 50, 50, 11))
            .ExpectProblemAsync(HttpStatusCode.Conflict, ErrorCodes.ReconciliationPending);

        // The user who counted cannot approve, however the name is written.
        await _client.PostAsUserAsync($"{url}/{reconciliationId}/approve", "alice")
            .ExpectProblemAsync(HttpStatusCode.Forbidden, ErrorCodes.FourEyesViolation);
        await _client.PostAsUserAsync($"{url}/{reconciliationId}/approve", " ALICE ")
            .ExpectProblemAsync(HttpStatusCode.Forbidden, ErrorCodes.FourEyesViolation);
        await _client.PostAsync($"{url}/{reconciliationId}/approve", null)
            .ExpectProblemAsync(HttpStatusCode.BadRequest, ErrorCodes.UserRequired);

        var approved = await _client.PostAsUserAsync($"{url}/{reconciliationId}/approve", "bob").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(("APPROVED", "bob"), ((string?)approved["status"], (string?)approved["decidedBy"]));
        await _client.PostAsUserAsync($"{url}/{reconciliationId}/approve", "carol")
            .ExpectProblemAsync(HttpStatusCode.Conflict, ErrorCodes.ReconciliationNotAwaitingApproval);

        await _client.SetStateAsync(atmId, "IN_SERVICE").ExpectAsync(HttpStatusCode.OK);

        var adjustments = await api.WithDbAsync(db => db.CassetteMovements.AsNoTracking()
            .Where(m => m.AtmId == atmId && m.Type == MovementType.ReconciliationAdjustment)
            .OrderBy(m => m.Id).Select(m => m.CountDelta).ToListAsync());
        Assert.Equal([-1, -3, 1], adjustments);
        await _client.AssertLedgerConsistentAsync(atmId);
    }

    [Fact(DisplayName = "B9: a rejected reconciliation keeps the ATM out of service until a recount")]
    public async Task Rejected_reconciliation_requires_a_recount()
    {
        var atmId = await _client.CreateAtmInServiceAsync(StandardLayout(), (1, 50));
        await _client.SetStateAsync(atmId, "MAINTENANCE").ExpectAsync(HttpStatusCode.OK);
        var url = $"/api/atms/{atmId}/reconciliations";

        // A typo: 5 instead of 50 notes.
        var wrong = await _client.PostAsUserAsync(url, "alice", CountRequest(5, 0, 0, 0)).ExpectAsync(HttpStatusCode.Created);
        await _client.PostAsUserAsync($"{url}/{wrong["id"]}/reject", "bob").ExpectAsync(HttpStatusCode.OK);
        await _client.SetStateAsync(atmId, "IN_SERVICE")
            .ExpectProblemAsync(HttpStatusCode.Conflict, ErrorCodes.ReconciliationBlocksService);

        // The recount restores the balance with a compensating adjustment; history is not rewritten.
        var recount = await _client.PostAsUserAsync(url, "alice", CountRequest(50, 0, 0, 0)).ExpectAsync(HttpStatusCode.Created);
        Assert.Equal("AWAITING_APPROVAL", (string?)recount["status"]);
        await _client.PostAsUserAsync($"{url}/{recount["id"]}/approve", "bob").ExpectAsync(HttpStatusCode.OK);
        await _client.SetStateAsync(atmId, "IN_SERVICE").ExpectAsync(HttpStatusCode.OK);

        Assert.Equal(50, (int)(await _client.CassetteAsync(atmId, 1))["noteCount"]!);
        await _client.AssertLedgerConsistentAsync(atmId);
    }

    [Fact(DisplayName = "B8: the consistency check reports a stored balance that differs from the ledger")]
    public async Task Consistency_check_detects_a_tampered_balance()
    {
        var atmId = await _client.CreateAtmInServiceAsync(StandardLayout(), (1, 30), (2, 30));
        var quote = await _client.QuoteAsync(atmId, NewCardNumber(), "TRY", 250).ExpectAsync(HttpStatusCode.Created);
        await _client.ConfirmAsync(atmId, (Guid)quote["quoteId"]!, Guid.NewGuid().ToString()).ExpectAsync(HttpStatusCode.OK);
        await _client.AssertLedgerConsistentAsync(atmId);

        // Every movement type so far is in the ledger, newest first.
        var movements = await _client.GetAsync($"/api/atms/{atmId}/movements").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(["LOAD", "RESERVATION", "WITHDRAWAL"],
            movements.AsArray().Select(m => (string?)m!["type"]).Distinct().Order());

        // Somebody changes a balance behind the application's back.
        await api.WithDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE cassette_stocks SET count = count + 1 WHERE cassette_id IN (SELECT id FROM cassettes WHERE atm_id = {atmId} AND slot = 1)"));

        var report = await _client.GetAsync($"/api/atms/{atmId}/consistency-check").ExpectAsync(HttpStatusCode.OK);
        Assert.False((bool)report["consistent"]!);
        var difference = Assert.Single(report["differences"]!.AsArray())!;
        Assert.Equal(1, (int)difference["slot"]!);
        Assert.Equal(29, (int)difference["storedCount"]!);
        Assert.Equal(28, (int)difference["ledgerCount"]!);

        // The ledger itself cannot be changed, not even with direct SQL.
        var error = await Assert.ThrowsAsync<PostgresException>(() => api.WithDbAsync(db =>
            db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM cassette_movements WHERE atm_id = {atmId}")));
        Assert.Contains("append-only", error.MessageText);

        // Put the balance back so the network-wide check stays clean for other tests.
        await api.WithDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE cassette_stocks SET count = count - 1 WHERE cassette_id IN (SELECT id FROM cassettes WHERE atm_id = {atmId} AND slot = 1)"));
        await _client.AssertLedgerConsistentAsync(atmId);
    }

    [Fact(DisplayName = "Health checks and the OpenAPI document are served")]
    public async Task Health_and_swagger_endpoints_respond()
    {
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/health/ready")).StatusCode);

        var swagger = await _client.GetAsync("/swagger/v1/swagger.json").ExpectAsync(HttpStatusCode.OK);
        Assert.NotNull(swagger["paths"]!["/api/atms/{atmId}/withdrawals/quotes"]);

        var unknown = await _client.GetAsync("/api/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("application/problem+json", unknown.Content.Headers.ContentType?.MediaType);
    }
}
