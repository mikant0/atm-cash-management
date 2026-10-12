namespace AtmCashManagement.Api.Domain;

public enum Currency
{
    TRY,
    USD,
    EUR
}

public enum AtmState
{
    Unconfigured,
    Configured,
    Loaded,
    InService,
    Maintenance
}

public enum CassetteType
{
    Recycling,
    Deposit
}

public enum MovementType
{
    Load,
    Unload,
    Withdrawal,
    Deposit,
    Reservation,
    ReservationRelease,
    ReconciliationAdjustment
}

public enum QuoteStatus
{
    Active,
    Confirmed,
    Cancelled,
    Expired
}

public enum QuoteOptionKind
{
    Exact,
    Lower,
    Upper
}

public enum ReconciliationStatus
{
    Completed,
    AwaitingApproval,
    Approved,
    Rejected
}

/// <summary>Operations whose availability depends on the ATM lifecycle state.</summary>
public enum AtmOperation
{
    ConfigureCassettes,
    LoadCash,
    UnloadCash,
    Reconcile,
    Withdraw,
    Deposit
}
