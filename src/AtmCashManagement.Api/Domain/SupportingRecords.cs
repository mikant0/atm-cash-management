namespace AtmCashManagement.Api.Domain;

/// <summary>Result of a confirm request, stored so that a retry with the same key gets the same answer.</summary>
public class IdempotencyRecord
{
    public string Key { get; set; } = null!;
    public Guid QuoteId { get; set; }

    /// <summary>What the key was first used for. A different request with the same key is rejected.</summary>
    public string RequestFingerprint { get; set; } = null!;
    public string ResponseBody { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
}

public class DepositTransaction
{
    public Guid Id { get; set; }
    public Guid AtmId { get; set; }
    public int UnrecognisedCount { get; set; }

    /// <summary>The response returned to the caller, kept for audit.</summary>
    public string Result { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Event waiting to be published. Written in the same transaction as the change that caused it.</summary>
public class OutboxMessage
{
    public const string LowCashAlert = "LowCashAlert";

    public long Id { get; set; }
    public string Type { get; set; } = null!;
    public string Payload { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
}
