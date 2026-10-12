using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AtmCashManagement.Api.Domain;
using Microsoft.EntityFrameworkCore;
using static AtmCashManagement.IntegrationTests.ApiClientExtensions;

namespace AtmCashManagement.IntegrationTests;

[Collection(ApiCollection.Name)]
public class WithdrawalTests(ApiFixture api)
{
    private readonly HttpClient _client = api.CreateClient();

    [Fact(DisplayName = "B6: parallel quotes on one ATM never reserve the same note twice")]
    public async Task Parallel_quotes_never_reserve_the_same_note_twice()
    {
        // Ten TRY 100 notes, twenty customers asking for one note each at the same moment.
        var atmId = await _client.CreateAtmInServiceAsync(StandardLayout(), (1, 10));

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            Task.Run(() => api.CreateClient().QuoteAsync(atmId, NewCardNumber(), "TRY", 100))));

        Assert.Equal(10, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(10, responses.Count(r => r.StatusCode == HttpStatusCode.UnprocessableEntity));
        foreach (var rejected in responses.Where(r => r.StatusCode == HttpStatusCode.UnprocessableEntity))
        {
            Assert.Equal(ErrorCodes.CannotDispense, (string?)(await rejected.ReadJsonAsync())["code"]);
        }

        var cassette = await _client.CassetteAsync(atmId, 1);
        Assert.Equal(10, (int)cassette["noteCount"]!);
        Assert.Equal(10, (int)cassette["reservedCount"]!);
        Assert.Equal(0, (int)cassette["availableCount"]!);
        await _client.AssertLedgerConsistentAsync(atmId);

        // Every winner can actually be paid: confirming all ten empties the cassette exactly.
        foreach (var accepted in responses.Where(r => r.StatusCode == HttpStatusCode.Created))
        {
            var quoteId = (Guid)(await accepted.ReadJsonAsync())["quoteId"]!;
            await _client.ConfirmAsync(atmId, quoteId, Guid.NewGuid().ToString()).ExpectAsync(HttpStatusCode.OK);
        }

        cassette = await _client.CassetteAsync(atmId, 1);
        Assert.Equal(0, (int)cassette["noteCount"]!);
        Assert.Equal(0, (int)cassette["reservedCount"]!);
        await _client.AssertLedgerConsistentAsync(atmId);
    }

    [Fact(DisplayName = "B6: parallel quotes for mixed amounts keep reservations within the stock")]
    public async Task Parallel_quotes_for_mixed_amounts_stay_within_the_stock()
    {
        var atmId = await _client.CreateAtmInServiceAsync(StandardLayout(), (1, 15), (2, 15), (3, 15));
        var amounts = new[] { 170, 250, 90, 320, 60, 500, 140, 210 };

        await Task.WhenAll(Enumerable.Range(0, 40).Select(i =>
            Task.Run(() => api.CreateClient().QuoteAsync(atmId, NewCardNumber(), "TRY", amounts[i % amounts.Length]))));

        foreach (var slot in new[] { 1, 2, 3 })
        {
            var cassette = await _client.CassetteAsync(atmId, slot);
            Assert.InRange((int)cassette["reservedCount"]!, 0, (int)cassette["noteCount"]!);
        }

        // What the quotes say they hold is exactly what the cassettes say is reserved.
        var held = await api.WithDbAsync(async db =>
            (await db.WithdrawalQuotes.AsNoTracking()
                .Where(q => q.AtmId == atmId && q.Status == QuoteStatus.Active).ToListAsync())
            .SelectMany(q => q.Reservations).Sum(r => r.Count));
        var reserved = 0;
        foreach (var slot in new[] { 1, 2, 3 })
        {
            reserved += (int)(await _client.CassetteAsync(atmId, slot))["reservedCount"]!;
        }

        Assert.Equal(held, reserved);
        await _client.AssertLedgerConsistentAsync(atmId);
    }

    [Fact(DisplayName = "B4: a repeated confirm with the same Idempotency-Key debits the cash once")]
    public async Task Confirm_is_idempotent()
    {
        var atmId = await _client.CreateAtmInServiceAsync(StandardLayout(), (1, 20), (2, 20), (3, 20));
        var quote = await _client.QuoteAsync(atmId, NewCardNumber(), "TRY", 270).ExpectAsync(HttpStatusCode.Created);
        var quoteId = (Guid)quote["quoteId"]!;
        var key = Guid.NewGuid().ToString();

        var first = await _client.ConfirmAsync(atmId, quoteId, key);
        var firstBody = await first.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.False(first.Headers.Contains("Idempotency-Replayed"));

        var second = await _client.ConfirmAsync(atmId, quoteId, key);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(firstBody, await second.Content.ReadAsStringAsync());
        Assert.Equal("true", second.Headers.GetValues("Idempotency-Replayed").Single());

        // 270 = 100x2 + 50x1 + 20x1, taken once.
        Assert.Equal(18, (int)(await _client.CassetteAsync(atmId, 1))["noteCount"]!);
        Assert.Equal(19, (int)(await _client.CassetteAsync(atmId, 2))["noteCount"]!);
        Assert.Equal(19, (int)(await _client.CassetteAsync(atmId, 3))["noteCount"]!);

        // A different key cannot confirm the quote a second time...
        await _client.ConfirmAsync(atmId, quoteId, Guid.NewGuid().ToString())
            .ExpectProblemAsync(HttpStatusCode.Conflict, ErrorCodes.QuoteAlreadyConfirmed);

        // ...the key cannot be reused for another request...
        var other = await _client.QuoteAsync(atmId, NewCardNumber(), "TRY", 100).ExpectAsync(HttpStatusCode.Created);
        await _client.ConfirmAsync(atmId, (Guid)other["quoteId"]!, key)
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, ErrorCodes.IdempotencyKeyReused);

        // ...and the header is mandatory.
        await _client.PostAsJsonAsync($"/api/atms/{atmId}/withdrawals/quotes/{other["quoteId"]}/confirm", new { })
            .ExpectProblemAsync(HttpStatusCode.BadRequest, ErrorCodes.IdempotencyKeyRequired);

        await _client.AssertLedgerConsistentAsync(atmId);
    }

    [Fact(DisplayName = "B4: parallel confirms with the same Idempotency-Key return one result")]
    public async Task Parallel_confirms_with_the_same_key_debit_once()
    {
        var atmId = await _client.CreateAtmInServiceAsync(StandardLayout(), (1, 20));
        var quote = await _client.QuoteAsync(atmId, NewCardNumber(), "TRY", 500).ExpectAsync(HttpStatusCode.Created);
        var quoteId = (Guid)quote["quoteId"]!;
        var key = Guid.NewGuid().ToString();

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            Task.Run(() => api.CreateClient().ConfirmAsync(atmId, quoteId, key))));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync()));
        Assert.Single(bodies.Distinct());
        Assert.Equal(7, responses.Count(r => r.Headers.Contains("Idempotency-Replayed")));

        Assert.Equal(15, (int)(await _client.CassetteAsync(atmId, 1))["noteCount"]!);
        var withdrawals = await api.WithDbAsync(db => db.CassetteMovements
            .Where(m => m.AtmId == atmId && m.Type == MovementType.Withdrawal).SumAsync(m => m.CountDelta));
        Assert.Equal(-5, withdrawals);
    }

    [Fact(DisplayName = "B4: reserved notes are not offered in another quote")]
    public async Task Reserved_notes_are_not_offered_again()
    {
        var atmId = await _client.CreateAtmInServiceAsync(StandardLayout(), (1, 3));

        var first = await _client.QuoteAsync(atmId, NewCardNumber(), "TRY", 200).ExpectAsync(HttpStatusCode.Created);
        Assert.True((bool)first["exactMatch"]!);

        // Only one note is left for the second customer.
        var second = await _client.QuoteAsync(atmId, NewCardNumber(), "TRY", 200).ExpectAsync(HttpStatusCode.Created);
        Assert.False((bool)second["exactMatch"]!);
        var option = Assert.Single(second["options"]!.AsArray())!;
        Assert.Equal("LOWER", (string?)option["option"]);
        Assert.Equal(100, (int)option["amount"]!);

        // Cancelling the first quote puts its notes back on offer.
        await _client.PostAsync($"/api/atms/{atmId}/withdrawals/quotes/{first["quoteId"]}/cancel", null)
            .ExpectAsync(HttpStatusCode.OK);
        var third = await _client.QuoteAsync(atmId, NewCardNumber(), "TRY", 200).ExpectAsync(HttpStatusCode.Created);
        Assert.True((bool)third["exactMatch"]!);

        await _client.ConfirmAsync(atmId, (Guid)first["quoteId"]!, Guid.NewGuid().ToString())
            .ExpectProblemAsync(HttpStatusCode.Conflict, ErrorCodes.QuoteCancelled);
        await _client.AssertLedgerConsistentAsync(atmId);
    }

    [Fact(DisplayName = "B3/B4: alternatives are both reserved; confirming one releases the rest")]
    public async Task Alternatives_are_reserved_and_the_unused_part_is_released()
    {
        // Acceptance test #2 through the API: 50x1, 20x2, request 80.
        var atmId = await _client.CreateAtmInServiceAsync(StandardLayout(), (2, 1), (3, 2));

        var quote = await _client.QuoteAsync(atmId, NewCardNumber(), "TRY", 80).ExpectAsync(HttpStatusCode.Created);
        var quoteId = (Guid)quote["quoteId"]!;
        var options = quote["options"]!.AsArray();
        Assert.False((bool)quote["exactMatch"]!);
        Assert.Equal(["LOWER:70", "UPPER:90"], options.Select(o => $"{o!["option"]}:{o["amount"]}"));

        Assert.Equal(1, (int)(await _client.CassetteAsync(atmId, 2))["reservedCount"]!);
        Assert.Equal(2, (int)(await _client.CassetteAsync(atmId, 3))["reservedCount"]!);

        await _client.ConfirmAsync(atmId, quoteId, Guid.NewGuid().ToString())
            .ExpectProblemAsync(HttpStatusCode.BadRequest, ErrorCodes.QuoteOptionRequired);
        await _client.ConfirmAsync(atmId, quoteId, Guid.NewGuid().ToString(), "EXACT")
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, ErrorCodes.QuoteOptionNotOffered);

        var withdrawal = await _client.ConfirmAsync(atmId, quoteId, Guid.NewGuid().ToString(), "LOWER")
            .ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(70, (int)withdrawal["dispensedAmount"]!);

        var fifties = await _client.CassetteAsync(atmId, 2);
        var twenties = await _client.CassetteAsync(atmId, 3);
        Assert.Equal((0, 0), ((int)fifties["noteCount"]!, (int)fifties["reservedCount"]!));
        Assert.Equal((1, 0), ((int)twenties["noteCount"]!, (int)twenties["reservedCount"]!));
        await _client.AssertLedgerConsistentAsync(atmId);
    }

    [Fact(DisplayName = "B4: an expired quote releases its reservation automatically and cannot be confirmed")]
    public async Task Expired_quote_is_released_and_cannot_be_confirmed()
    {
        var atmId = await _client.CreateAtmInServiceAsync(StandardLayout(), (1, 5));
        var quote = await _client.QuoteAsync(atmId, NewCardNumber(), "TRY", 300).ExpectAsync(HttpStatusCode.Created);
        var quoteId = (Guid)quote["quoteId"]!;
        Assert.Equal(60.0, ((DateTimeOffset)quote["expiresAt"]! - (DateTimeOffset)quote["createdAt"]!).TotalSeconds);
        Assert.Equal(3, (int)(await _client.CassetteAsync(atmId, 1))["reservedCount"]!);

        api.Clock.Advance(TimeSpan.FromSeconds(61));

        // No request touches the ATM here: the background sweeper has to do the release.
        var released = false;
        for (var attempt = 0; attempt < 50 && !released; attempt++)
        {
            await Task.Delay(200);
            released = (int)(await _client.CassetteAsync(atmId, 1))["reservedCount"]! == 0;
        }

        Assert.True(released, "The reservation of the expired quote was not released within 10 seconds.");
        var stored = await _client.GetAsync($"/api/atms/{atmId}/withdrawals/quotes/{quoteId}").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("EXPIRED", (string?)stored["status"]);

        await _client.ConfirmAsync(atmId, quoteId, Guid.NewGuid().ToString())
            .ExpectProblemAsync(HttpStatusCode.Conflict, ErrorCodes.QuoteExpired);
        Assert.Equal(5, (int)(await _client.CassetteAsync(atmId, 1))["noteCount"]!);
        await _client.AssertLedgerConsistentAsync(atmId);
    }

    [Fact(DisplayName = "B4: an overdue quote does not block a new quote even before the sweeper runs")]
    public async Task Overdue_reservation_is_released_by_the_next_quote()
    {
        var atmId = await _client.CreateAtmInServiceAsync(StandardLayout(), (1, 2));
        await _client.QuoteAsync(atmId, NewCardNumber(), "TRY", 200).ExpectAsync(HttpStatusCode.Created);

        api.Clock.Advance(TimeSpan.FromSeconds(61));

        var next = await _client.QuoteAsync(atmId, NewCardNumber(), "TRY", 200).ExpectAsync(HttpStatusCode.Created);
        Assert.True((bool)next["exactMatch"]!);
        Assert.Equal(2, (int)(await _client.CassetteAsync(atmId, 1))["reservedCount"]!);
        await _client.AssertLedgerConsistentAsync(atmId);
    }

    [Fact(DisplayName = "B5: the daily limit is per card and currency and counts unconfirmed quotes")]
    public async Task Daily_limit_is_enforced_at_the_quote_step()
    {
        var atmId = await _client.CreateAtmInServiceAsync(StandardLayout(), (1, 50), (4, 30));
        var card = NewCardNumber();

        // USD limit is 1,000. An unconfirmed quote of 600 already counts.
        var first = await _client.QuoteAsync(atmId, card, "USD", 600).ExpectAsync(HttpStatusCode.Created);
        var problem = await _client.QuoteAsync(atmId, card, "USD", 500)
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, ErrorCodes.DailyLimitExceeded);
        Assert.Equal(400, (int)problem["remaining"]!);

        await _client.ConfirmAsync(atmId, (Guid)first["quoteId"]!, Guid.NewGuid().ToString()).ExpectAsync(HttpStatusCode.OK);

        // 350 cannot be paid with 100s. Upper (400) fits the remaining 400, so it is offered.
        var alternatives = await _client.QuoteAsync(atmId, card, "USD", 350).ExpectAsync(HttpStatusCode.Created);
        Assert.Equal(["LOWER:300", "UPPER:400"],
            alternatives["options"]!.AsArray().Select(o => $"{o!["option"]}:{o["amount"]}"));
        await _client.ConfirmAsync(atmId, (Guid)alternatives["quoteId"]!, Guid.NewGuid().ToString(), "LOWER")
            .ExpectAsync(HttpStatusCode.OK);

        // 900 used. 50 cannot be paid; upper would be 100, which still fits. 150 does not fit at all.
        var last = await _client.QuoteAsync(atmId, card, "USD", 50).ExpectAsync(HttpStatusCode.Created);
        Assert.Equal(["UPPER:100"], last["options"]!.AsArray().Select(o => $"{o!["option"]}:{o["amount"]}"));
        await _client.PostAsync($"/api/atms/{atmId}/withdrawals/quotes/{last["quoteId"]}/cancel", null)
            .ExpectAsync(HttpStatusCode.OK);
        await _client.QuoteAsync(atmId, card, "USD", 150)
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, ErrorCodes.DailyLimitExceeded);

        // The limit follows the card to another ATM, but TRY has its own limit.
        var otherAtm = await _client.CreateAtmInServiceAsync(StandardLayout(), (4, 10));
        await _client.QuoteAsync(otherAtm, card, "USD", 200)
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, ErrorCodes.DailyLimitExceeded);
        await _client.QuoteAsync(atmId, card, "TRY", 1000).ExpectAsync(HttpStatusCode.Created);

        // Another card is not affected.
        await _client.QuoteAsync(atmId, NewCardNumber(), "USD", 1000).ExpectAsync(HttpStatusCode.Created);
    }

    [Fact(DisplayName = "B5: an upper alternative above the remaining limit is not offered")]
    public async Task Upper_alternative_respects_the_daily_limit()
    {
        object[] layout =
        [
            Recycling(1, "TRY", 100), Recycling(2, "TRY", 50), Recycling(3, "USD", 20), Recycling(4, "USD", 500),
            DepositCassette(5)
        ];
        var atmId = await _client.CreateAtmInServiceAsync(layout, (3, 1), (4, 5));
        var card = NewCardNumber();

        // Use up 20 of the 1,000 limit (and the only USD 20 note): 980 remain, only 500s are left.
        var first = await _client.QuoteAsync(atmId, card, "USD", 20).ExpectAsync(HttpStatusCode.Created);
        await _client.ConfirmAsync(atmId, (Guid)first["quoteId"]!, Guid.NewGuid().ToString()).ExpectAsync(HttpStatusCode.OK);

        // 600 is not payable. Upper would be 1,000, which is above the remaining 980: lower only.
        var limited = await _client.QuoteAsync(atmId, card, "USD", 600).ExpectAsync(HttpStatusCode.Created);
        Assert.Equal(["LOWER:500"], limited["options"]!.AsArray().Select(o => $"{o!["option"]}:{o["amount"]}"));

        // A card with its full limit gets both alternatives for the same request.
        var fresh = await _client.QuoteAsync(atmId, NewCardNumber(), "USD", 600).ExpectAsync(HttpStatusCode.Created);
        Assert.Equal(["LOWER:500", "UPPER:1000"], fresh["options"]!.AsArray().Select(o => $"{o!["option"]}:{o["amount"]}"));
    }

    [Fact(DisplayName = "B5: the card number is stored only as a keyed hash")]
    public async Task Card_number_is_not_stored_in_plain_text()
    {
        var atmId = await _client.CreateAtmInServiceAsync(StandardLayout(), (1, 5));
        var card = NewCardNumber();
        var quote = await _client.QuoteAsync(atmId, card, "TRY", 100).ExpectAsync(HttpStatusCode.Created);
        var quoteId = (Guid)quote["quoteId"]!;

        Assert.DoesNotContain(card, quote.ToJsonString());

        // Look at the raw row, every column, exactly as it is stored.
        var row = await api.WithDbAsync(async db =>
        {
            var connection = db.Database.GetDbConnection();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT row_to_json(q)::text FROM withdrawal_quotes q WHERE id = '{quoteId}'";
            return (string)(await command.ExecuteScalarAsync())!;
        });
        Assert.DoesNotContain(card, row);
        Assert.Contains(card[^4..], row);

        var stored = await api.WithDbAsync(db => db.WithdrawalQuotes.AsNoTracking().SingleAsync(q => q.Id == quoteId));
        Assert.Matches("^[0-9a-f]{64}$", stored.CardHash);
    }

    [Fact(DisplayName = "B2: duplicate cassettes dispense from the fuller one first")]
    public async Task Duplicate_cassettes_dispense_from_the_fuller_one_first()
    {
        object[] layout =
        [
            Recycling(1, "TRY", 100), Recycling(2, "TRY", 100), Recycling(3, "TRY", 20), Recycling(4, "EUR", 50),
            DepositCassette(5)
        ];
        var atmId = await _client.CreateAtmInServiceAsync(layout, (1, 40), (2, 80));

        var quote = await _client.QuoteAsync(atmId, NewCardNumber(), "TRY", 3000).ExpectAsync(HttpStatusCode.Created);
        var lines = quote["options"]![0]!["cassettes"]!.AsArray();

        var line = Assert.Single(lines)!;
        Assert.Equal(2, (int)line["slot"]!);
        Assert.Equal(30, (int)line["count"]!);
    }

    [Fact(DisplayName = "B10: a low-cash alert goes through the outbox and is marked as sent")]
    public async Task Low_cash_alert_is_written_to_the_outbox_and_dispatched()
    {
        var atmId = await _client.CreateAtmInServiceAsync(StandardLayout(), (1, 12));

        // 12 -> 9 available: crosses the threshold of 10 (10% of 100).
        await _client.QuoteAsync(atmId, NewCardNumber(), "TRY", 300).ExpectAsync(HttpStatusCode.Created);
        // Already low: no second alert.
        await _client.QuoteAsync(atmId, NewCardNumber(), "TRY", 100).ExpectAsync(HttpStatusCode.Created);

        JsonNode? payload = null;
        DateTimeOffset? sentAt = null;
        for (var attempt = 0; attempt < 50 && sentAt is null; attempt++)
        {
            await Task.Delay(200);
            var alerts = await api.WithDbAsync(async db =>
                (await db.OutboxMessages.AsNoTracking().Where(m => m.Type == OutboxMessage.LowCashAlert).ToListAsync())
                .Where(m => m.Payload.Contains(atmId.ToString())).ToList());

            var alert = Assert.Single(alerts);
            payload = JsonNode.Parse(alert.Payload);
            sentAt = alert.SentAt;
        }

        Assert.NotNull(sentAt);
        Assert.Equal(1, (int)payload!["slot"]!);
        Assert.Equal(9, (int)payload["availableCount"]!);
        Assert.Equal(10, (int)payload["threshold"]!);
    }
}
