namespace AtmCashManagement.Api.Domain;

public class Reconciliation
{
    public Guid Id { get; set; }
    public Guid AtmId { get; set; }
    public ReconciliationStatus Status { get; set; }

    /// <summary>Sum of the absolute differences, valued in TRY with the rates at the time of the count.</summary>
    public decimal TotalDifferenceTry { get; set; }

    public string CreatedBy { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public string? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }

    public List<ReconciliationLine> Lines { get; set; } = [];
}

public sealed class ReconciliationLine
{
    public Guid CassetteId { get; set; }
    public int Slot { get; set; }
    public Currency Currency { get; set; }
    public int Denomination { get; set; }
    public int SystemCount { get; set; }
    public int CountedCount { get; set; }
    public int Difference { get; set; }
    public decimal RateToTry { get; set; }
    public decimal DifferenceValueTry { get; set; }
}

public class ExchangeRate
{
    public Currency Currency { get; set; }

    /// <summary>Value of one unit of the currency in TRY.</summary>
    public decimal RateToTry { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
