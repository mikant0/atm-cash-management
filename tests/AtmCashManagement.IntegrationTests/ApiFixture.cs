using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AtmCashManagement.Api.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.PostgreSql;

namespace AtmCashManagement.IntegrationTests;

/// <summary>
/// Runs the real API in-process against a real PostgreSQL started with Testcontainers (Docker).
/// The schema is created by the same startup migration that production uses.
/// </summary>
public sealed class ApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    public TestClock Clock { get; } = new();

    public Task InitializeAsync() => _postgres.StartAsync();

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Default", _postgres.GetConnectionString());
        builder.UseSetting("Cash:QuoteSweepIntervalSeconds", "1");
        builder.UseSetting("Cash:OutboxPollIntervalSeconds", "1");

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });
    }

    public async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>
{
    public const string Name = "api";
}

/// <summary>A clock the tests can move forward, so a 60 second expiry does not need a 60 second wait.</summary>
public sealed class TestClock : TimeProvider
{
    private long _offsetTicks;

    public override DateTimeOffset GetUtcNow() =>
        DateTimeOffset.UtcNow.AddTicks(Interlocked.Read(ref _offsetTicks));

    public void Advance(TimeSpan by) => Interlocked.Add(ref _offsetTicks, by.Ticks);
}

/// <summary>Small helpers that keep the tests readable. Every test creates its own ATM and cards.</summary>
public static class ApiClientExtensions
{
    private static int _cardSequence;

    public static string NewCardNumber() =>
        $"5400{Random.Shared.Next(100000, 999999)}{Interlocked.Increment(ref _cardSequence):D6}";

    public static object Recycling(int slot, string currency, int denomination) =>
        new { slot, type = "RECYCLING", currency, denomination };

    public static object DepositCassette(int slot) => new { slot, type = "DEPOSIT" };

    /// <summary>Slots 1-4: TRY 100, TRY 50, TRY 20, USD 100. Slot 5: deposit.</summary>
    public static object[] StandardLayout() =>
    [
        Recycling(1, "TRY", 100), Recycling(2, "TRY", 50), Recycling(3, "TRY", 20), Recycling(4, "USD", 100),
        DepositCassette(5)
    ];

    public static async Task<JsonNode> ReadJsonAsync(this HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonNode>())!;

    public static async Task<JsonNode> ExpectAsync(this Task<HttpResponseMessage> request, HttpStatusCode expected)
    {
        var response = await request;
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Expected {(int)expected} but got {(int)response.StatusCode}: {body}");
        return JsonNode.Parse(body)!;
    }

    public static async Task<JsonNode> ExpectProblemAsync(this Task<HttpResponseMessage> request,
        HttpStatusCode expectedStatus, string expectedCode)
    {
        var response = await request;
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expectedStatus,
            $"Expected {(int)expectedStatus} but got {(int)response.StatusCode}: {body}");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = JsonNode.Parse(body)!;
        Assert.Equal(expectedCode, (string?)problem["code"]);
        Assert.Equal((int)expectedStatus, (int?)problem["status"]);
        Assert.False(string.IsNullOrWhiteSpace((string?)problem["detail"]));
        Assert.False(string.IsNullOrWhiteSpace((string?)problem["traceId"]));
        return problem;
    }

    public static async Task<Guid> CreateAtmAsync(this HttpClient client)
    {
        var atm = await client.PostAsJsonAsync("/api/atms", new { code = $"T-{Guid.NewGuid():N}"[..20], location = "Test" })
            .ExpectAsync(HttpStatusCode.Created);
        return (Guid)atm["id"]!;
    }

    /// <summary>Creates an ATM, configures it, loads the given cash and puts it in service.</summary>
    public static async Task<Guid> CreateAtmInServiceAsync(this HttpClient client, object[] layout,
        params (int Slot, int Count)[] cash)
    {
        var atmId = await client.CreateAtmAsync();
        await client.PutAsJsonAsync($"/api/atms/{atmId}/cassettes", new { cassettes = layout }).ExpectAsync(HttpStatusCode.OK);
        await client.PostAsJsonAsync($"/api/atms/{atmId}/cassettes/load",
            new { items = cash.Select(c => new { slot = c.Slot, count = c.Count }) }).ExpectAsync(HttpStatusCode.OK);
        await client.SetStateAsync(atmId, "IN_SERVICE").ExpectAsync(HttpStatusCode.OK);
        return atmId;
    }

    public static Task<HttpResponseMessage> SetStateAsync(this HttpClient client, Guid atmId, string targetState) =>
        client.PostAsJsonAsync($"/api/atms/{atmId}/state", new { targetState });

    public static Task<HttpResponseMessage> QuoteAsync(this HttpClient client, Guid atmId, string cardNumber,
        string currency, int amount) =>
        client.PostAsJsonAsync($"/api/atms/{atmId}/withdrawals/quotes", new { cardNumber, currency, amount });

    public static Task<HttpResponseMessage> ConfirmAsync(this HttpClient client, Guid atmId, Guid quoteId,
        string idempotencyKey, string? option = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/atms/{atmId}/withdrawals/quotes/{quoteId}/confirm")
        {
            Content = JsonContent.Create(new { option })
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return client.SendAsync(request);
    }

    public static Task<HttpResponseMessage> PostAsUserAsync(this HttpClient client, string url, string user,
        object? body = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        request.Headers.Add("X-User", user);
        return client.SendAsync(request);
    }

    public static async Task<JsonNode> CassetteAsync(this HttpClient client, Guid atmId, int slot)
    {
        var atm = await client.GetAsync($"/api/atms/{atmId}").ExpectAsync(HttpStatusCode.OK);
        return atm["cassettes"]!.AsArray().Single(c => (int)c!["slot"]! == slot)!;
    }

    public static async Task AssertLedgerConsistentAsync(this HttpClient client, Guid atmId)
    {
        var report = await client.GetAsync($"/api/atms/{atmId}/consistency-check").ExpectAsync(HttpStatusCode.OK);
        Assert.True((bool)report["consistent"]!, $"Ledger and stored balances differ: {report}");
    }

    /// <summary>Counts for a reconciliation where every recycling cassette matches the given numbers.</summary>
    public static object CountRequest(int slot1, int slot2, int slot3, int slot4, params object[] depositCounts) => new
    {
        cassettes = new object[]
        {
            new { slot = 1, countedNotes = slot1 },
            new { slot = 2, countedNotes = slot2 },
            new { slot = 3, countedNotes = slot3 },
            new { slot = 4, countedNotes = slot4 },
            new { slot = 5, depositCounts }
        }
    };
}
