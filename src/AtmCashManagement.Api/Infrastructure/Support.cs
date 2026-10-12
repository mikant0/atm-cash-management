using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AtmCashManagement.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Options;

namespace AtmCashManagement.Api.Infrastructure;

public static class JsonDefaults
{
    /// <summary>One JSON shape for API bodies and jsonb columns: camelCase, enums as UPPER_SNAKE strings.</summary>
    public static readonly JsonSerializerOptions Options = Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper));
        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        return options;
    }
}

public sealed class CashOptions
{
    public const string SectionName = "Cash";

    public int QuoteTtlSeconds { get; set; } = 60;

    public Dictionary<Currency, int> DailyLimits { get; set; } = new()
    {
        [Currency.TRY] = 20_000,
        [Currency.USD] = 1_000,
        [Currency.EUR] = 1_000
    };

    /// <summary>A reconciliation whose differences are worth more than this needs a second user's approval.</summary>
    public decimal ReconciliationApprovalThresholdTry { get; set; } = 1_000m;

    /// <summary>A low-cash alert is raised when a recycling cassette's available notes fall below this share of its capacity.</summary>
    public double LowCashThresholdRatio { get; set; } = 0.10;

    /// <summary>Time zone that decides when a card's "day" starts and ends.</summary>
    public string BusinessTimeZone { get; set; } = "Europe/Istanbul";

    /// <summary>Secret key for the card number HMAC. Must come from a secret store in production.</summary>
    public string CardHashKey { get; set; } = "";

    public int QuoteSweepIntervalSeconds { get; set; } = 5;
    public int OutboxPollIntervalSeconds { get; set; } = 5;
}

/// <summary>
/// Turns a card number into a keyed hash (HMAC-SHA256). The same card always gives the same hash,
/// which is all the daily limit needs. A plain SHA-256 would not be enough: card numbers have so
/// little entropy that every possible number can be hashed and looked up; the secret key prevents that.
/// </summary>
public sealed class CardHasher
{
    private readonly byte[] _key;

    public CardHasher(IOptions<CashOptions> options)
    {
        if (string.IsNullOrWhiteSpace(options.Value.CardHashKey))
        {
            throw new InvalidOperationException("Cash:CardHashKey is not configured.");
        }

        _key = Encoding.UTF8.GetBytes(options.Value.CardHashKey);
    }

    public string Hash(string cardNumber)
    {
        var digits = Normalize(cardNumber);
        return Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(digits))).ToLowerInvariant();
    }

    public static string Last4(string cardNumber) => Normalize(cardNumber)[^4..];

    public static string Normalize(string cardNumber) => cardNumber.Replace(" ", "").Replace("-", "");
}

public sealed class BusinessCalendar
{
    private readonly TimeZoneInfo _zone;

    public BusinessCalendar(IOptions<CashOptions> options)
    {
        _zone = TimeZoneInfo.FindSystemTimeZoneById(options.Value.BusinessTimeZone);
    }

    public DateOnly BusinessDateOf(DateTimeOffset instant) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, _zone).DateTime);
}

/// <summary>Lets "dotnet ef migrations add" build the model without starting the application.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=atm_cash;Username=atm;Password=atm")
            .UseSnakeCaseNamingConvention()
            .Options;

        return new AppDbContext(options);
    }
}
