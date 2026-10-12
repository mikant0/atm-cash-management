namespace AtmCashManagement.Api.Domain;

public static class Banknotes
{
    public static readonly IReadOnlyList<int> Denominations = [20, 50, 100, 500];

    public const int CassetteCapacity = 100;
    public const int CassettesPerAtm = 5;
    public const int RecyclingCassettesPerAtm = 4;
    public const int MinForeignCurrencyCassettes = 1;
    public const int MaxForeignCurrencyCassettes = 2;

    public static bool IsSupported(int denomination) => Denominations.Contains(denomination);

    public static bool IsForeign(Currency currency) => currency != Currency.TRY;
}
