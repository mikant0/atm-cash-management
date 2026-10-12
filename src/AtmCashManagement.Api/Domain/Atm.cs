namespace AtmCashManagement.Api.Domain;

public class Atm
{
    public Guid Id { get; set; }
    public string Code { get; set; } = null!;
    public string? Location { get; set; }
    public AtmState State { get; set; } = AtmState.Unconfigured;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<Cassette> Cassettes { get; set; } = [];

    public IEnumerable<Cassette> GetRecyclingCassettes() =>
        Cassettes.Where(c => c.Type == CassetteType.Recycling);

    public IEnumerable<Cassette> GetRecyclingCassettes(Currency currency, int denomination) =>
        Cassettes.Where(c => c.Holds(currency, denomination));

    public Cassette? GetDepositCassette() =>
        Cassettes.FirstOrDefault(c => c.Type == CassetteType.Deposit);

    public Cassette? GetCassette(int slot) => Cassettes.FirstOrDefault(c => c.Slot == slot);
}
