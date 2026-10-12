namespace AtmCashManagement.Api.Domain;

public class WithdrawalQuote
{
    public Guid Id { get; set; }
    public Guid AtmId { get; set; }

    /// <summary>Keyed hash of the card number. The card number itself is never stored.</summary>
    public string CardHash { get; set; } = null!;
    public string CardLast4 { get; set; } = null!;

    public Currency Currency { get; set; }
    public int RequestedAmount { get; set; }
    public QuoteStatus Status { get; set; }

    /// <summary>
    /// Amount counted against the daily limit while the quote is active: the largest amount
    /// the customer could still confirm.
    /// </summary>
    public int HeldAmount { get; set; }

    /// <summary>Business day (bank local time) whose daily limit this quote consumes.</summary>
    public DateOnly BusinessDate { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public QuoteOptionKind? ConfirmedOption { get; set; }
    public int? ConfirmedAmount { get; set; }

    /// <summary>The breakdowns offered to the customer: one EXACT, or LOWER and/or UPPER.</summary>
    public List<QuoteOption> Options { get; set; } = [];

    /// <summary>Notes held per cassette. Covers whichever option the customer confirms.</summary>
    public List<QuoteReservation> Reservations { get; set; } = [];

    public bool IsExpiredAt(DateTimeOffset now) => now >= ExpiresAt;
}

public sealed class QuoteOption
{
    public QuoteOptionKind Kind { get; set; }
    public int Amount { get; set; }
    public List<QuoteOptionLine> Lines { get; set; } = [];
}

public sealed class QuoteOptionLine
{
    public Guid CassetteId { get; set; }
    public int Slot { get; set; }
    public int Denomination { get; set; }
    public int Count { get; set; }
}

public sealed class QuoteReservation
{
    public Guid CassetteId { get; set; }
    public int Denomination { get; set; }
    public int Count { get; set; }
}
