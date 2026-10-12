namespace AtmCashManagement.Api.Domain;

/// <summary>
/// Append-only ledger entry. A cassette's stored balance is the sum of its movements:
/// Count = SUM(CountDelta) and Reserved = SUM(ReservedDelta).
/// </summary>
public class CassetteMovement
{
    public long Id { get; private set; }
    public Guid AtmId { get; private set; }
    public Guid CassetteId { get; private set; }
    public Currency Currency { get; private set; }
    public int Denomination { get; private set; }
    public MovementType Type { get; private set; }

    /// <summary>Change in the number of notes physically in the cassette.</summary>
    public int CountDelta { get; private set; }

    /// <summary>Change in the number of notes reserved for quotes.</summary>
    public int ReservedDelta { get; private set; }

    /// <summary>Business record that caused the movement, e.g. "WithdrawalQuote".</summary>
    public string? ReferenceType { get; private set; }
    public Guid? ReferenceId { get; private set; }
    public string? PerformedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private CassetteMovement()
    {
    }

    public CassetteMovement(Guid atmId, Guid cassetteId, Currency currency, int denomination, MovementType type,
        int countDelta, int reservedDelta, string? referenceType, Guid? referenceId, string? performedBy,
        DateTimeOffset createdAt)
    {
        AtmId = atmId;
        CassetteId = cassetteId;
        Currency = currency;
        Denomination = denomination;
        Type = type;
        CountDelta = countDelta;
        ReservedDelta = reservedDelta;
        ReferenceType = referenceType;
        ReferenceId = referenceId;
        PerformedBy = performedBy;
        CreatedAt = createdAt;
    }
}
