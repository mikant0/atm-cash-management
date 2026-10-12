namespace AtmCashManagement.Api.Domain;

public class Cassette
{
    public Guid Id { get; set; }
    public Guid AtmId { get; set; }

    /// <summary>Physical position in the ATM, 1..5.</summary>
    public int Slot { get; set; }

    public CassetteType Type { get; set; }

    /// <summary>Configured currency. Null for the deposit cassette, which accepts everything.</summary>
    public Currency? Currency { get; set; }

    /// <summary>Configured denomination. Null for the deposit cassette.</summary>
    public int? Denomination { get; set; }

    public int Capacity { get; set; } = Banknotes.CassetteCapacity;

    /// <summary>
    /// Note counts per currency and denomination. A recycling cassette has a single active line;
    /// the deposit cassette has one line for every kind of note it has received.
    /// </summary>
    public List<CassetteStock> Stocks { get; set; } = [];

    public int TotalCount() => Stocks.Sum(s => s.Count);

    public int TotalReserved() => Stocks.Sum(s => s.Reserved);

    /// <summary>Notes that can still be offered in a quote. The deposit cassette never dispenses.</summary>
    public int AvailableCount() => Type == CassetteType.Recycling ? TotalCount() - TotalReserved() : 0;

    public int FreeSpace() => Capacity - TotalCount();

    public bool Holds(Currency currency, int denomination) =>
        Type == CassetteType.Recycling && Currency == currency && Denomination == denomination;

    public CassetteStock? GetStock(Currency currency, int denomination) =>
        Stocks.FirstOrDefault(s => s.Currency == currency && s.Denomination == denomination);
}

/// <summary>Stored balance of one kind of note in a cassette. Always changed together with a movement.</summary>
public class CassetteStock
{
    public Guid Id { get; set; }
    public Guid CassetteId { get; set; }
    public Currency Currency { get; set; }
    public int Denomination { get; set; }

    /// <summary>Notes physically in the cassette.</summary>
    public int Count { get; set; }

    /// <summary>Notes promised to active quotes. Always between 0 and <see cref="Count"/>.</summary>
    public int Reserved { get; set; }
}
