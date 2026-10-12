namespace AtmCashManagement.Api.Domain;

public sealed record CassetteSpec(int Slot, CassetteType Type, Currency? Currency, int? Denomination);

/// <summary>Rules a full set of five cassettes must satisfy (Part A, items 2-6).</summary>
public static class CassetteLayoutRules
{
    public static IReadOnlyList<string> Validate(IReadOnlyCollection<CassetteSpec> cassettes)
    {
        var errors = new List<string>();

        if (cassettes.Count != Banknotes.CassettesPerAtm)
        {
            errors.Add($"An ATM has exactly {Banknotes.CassettesPerAtm} cassettes, but {cassettes.Count} were given.");
        }

        var slots = cassettes.Select(c => c.Slot).ToList();
        if (slots.Any(s => s < 1 || s > Banknotes.CassettesPerAtm) || slots.Distinct().Count() != slots.Count)
        {
            errors.Add($"Slots must be the numbers 1 to {Banknotes.CassettesPerAtm}, each used once.");
        }

        var depositCount = cassettes.Count(c => c.Type == CassetteType.Deposit);
        if (depositCount != 1)
        {
            errors.Add($"Exactly one cassette must be the deposit cassette, but {depositCount} were given.");
        }

        foreach (var deposit in cassettes.Where(c => c.Type == CassetteType.Deposit))
        {
            if (deposit.Currency is not null || deposit.Denomination is not null)
            {
                errors.Add($"Slot {deposit.Slot}: the deposit cassette accepts every note and must not " +
                           "have a currency or denomination.");
            }
        }

        var recycling = cassettes.Where(c => c.Type == CassetteType.Recycling).ToList();
        foreach (var cassette in recycling)
        {
            if (cassette.Currency is null || cassette.Denomination is null)
            {
                errors.Add($"Slot {cassette.Slot}: a recycling cassette needs a currency and a denomination.");
            }
            else if (!Banknotes.IsSupported(cassette.Denomination.Value))
            {
                errors.Add($"Slot {cassette.Slot}: {cassette.Denomination} is not a supported denomination. " +
                           $"Supported: {string.Join(", ", Banknotes.Denominations)}.");
            }
        }

        var foreignCount = recycling.Count(c => c.Currency is { } currency && Banknotes.IsForeign(currency));
        if (foreignCount is < Banknotes.MinForeignCurrencyCassettes or > Banknotes.MaxForeignCurrencyCassettes)
        {
            errors.Add($"Between {Banknotes.MinForeignCurrencyCassettes} and {Banknotes.MaxForeignCurrencyCassettes} " +
                       $"recycling cassettes must hold a foreign currency (USD or EUR), but {foreignCount} do.");
        }

        return errors;
    }

    public static void EnsureValid(IReadOnlyCollection<CassetteSpec> cassettes)
    {
        var errors = Validate(cassettes);
        if (errors.Count > 0)
        {
            throw new DomainException(ErrorCodes.InvalidCassetteLayout,
                "The cassette configuration is not valid.",
                422,
                new Dictionary<string, object?> { ["violations"] = errors });
        }
    }
}
