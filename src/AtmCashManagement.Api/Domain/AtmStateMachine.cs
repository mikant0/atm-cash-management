namespace AtmCashManagement.Api.Domain;

/// <summary>
/// ATM lifecycle:
///
///   UNCONFIGURED --configure--> CONFIGURED --load--> LOADED --> IN_SERVICE &lt;--> MAINTENANCE
///                                                      \------------------------^
///
/// CONFIGURED and LOADED are reached as a side effect of configuring cassettes and loading cash.
/// IN_SERVICE and MAINTENANCE are requested explicitly.
/// </summary>
public static class AtmStateMachine
{
    private static readonly HashSet<(AtmState From, AtmState To)> RequestableTransitions =
    [
        (AtmState.Loaded, AtmState.InService),
        (AtmState.Loaded, AtmState.Maintenance),
        (AtmState.InService, AtmState.Maintenance),
        (AtmState.Maintenance, AtmState.InService)
    ];

    private static readonly Dictionary<AtmOperation, AtmState[]> AllowedStates = new()
    {
        [AtmOperation.ConfigureCassettes] = [AtmState.Unconfigured, AtmState.Configured, AtmState.Maintenance],
        [AtmOperation.LoadCash] = [AtmState.Configured, AtmState.Maintenance],
        [AtmOperation.UnloadCash] = [AtmState.Maintenance],
        [AtmOperation.Reconcile] = [AtmState.Configured, AtmState.Maintenance],
        [AtmOperation.Withdraw] = [AtmState.InService],
        [AtmOperation.Deposit] = [AtmState.InService]
    };

    public static bool CanTransition(AtmState from, AtmState to) => RequestableTransitions.Contains((from, to));

    public static void EnsureTransition(AtmState from, AtmState to)
    {
        if (CanTransition(from, to))
        {
            return;
        }

        var reachable = RequestableTransitions.Where(t => t.From == from).Select(t => EnumText.Of(t.To)).ToArray();
        var hint = reachable.Length == 0
            ? "No state can be requested from here; finish the initial setup (configure cassettes, load cash) first."
            : $"Allowed targets: {string.Join(", ", reachable)}.";

        throw new DomainException(ErrorCodes.InvalidStateTransition,
            $"An ATM in state {EnumText.Of(from)} cannot move to {EnumText.Of(to)}. {hint}",
            409,
            new Dictionary<string, object?>
            {
                ["currentState"] = EnumText.Of(from),
                ["requestedState"] = EnumText.Of(to),
                ["allowedStates"] = reachable
            });
    }

    public static bool IsAllowed(AtmState state, AtmOperation operation) => AllowedStates[operation].Contains(state);

    public static void EnsureOperationAllowed(AtmState state, AtmOperation operation)
    {
        if (IsAllowed(state, operation))
        {
            return;
        }

        var allowed = AllowedStates[operation].Select(EnumText.Of).ToArray();
        throw new DomainException(ErrorCodes.OperationNotAllowedInState,
            $"{EnumText.Of(operation)} is not allowed while the ATM is {EnumText.Of(state)}. " +
            $"Allowed states: {string.Join(", ", allowed)}.",
            409,
            new Dictionary<string, object?>
            {
                ["currentState"] = EnumText.Of(state),
                ["operation"] = EnumText.Of(operation),
                ["allowedStates"] = allowed
            });
    }
}
