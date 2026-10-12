using AtmCashManagement.Api.Domain;
using static AtmCashManagement.UnitTests.NoteSelectorAcceptanceTests;

namespace AtmCashManagement.UnitTests;

public class NoteSelectorEdgeCaseTests
{
    [Fact]
    public void Upper_alternative_is_not_offered_above_the_remaining_daily_limit()
    {
        // 80 cannot be paid; 90 could, but the customer may only take 85 more today.
        var result = NoteSelector.Select(Notes((50, 1), (20, 2)), 80, maxUpperAmount: 85);

        AssertBreakdown(result.Lower, 70, (50, 1), (20, 1));
        Assert.Null(result.Upper);
    }

    [Fact]
    public void Upper_alternative_is_offered_when_it_fits_the_remaining_daily_limit()
    {
        var result = NoteSelector.Select(Notes((50, 1), (20, 2)), 80, maxUpperAmount: 90);

        AssertBreakdown(result.Upper, 90, (50, 1), (20, 2));
    }

    [Fact]
    public void Empty_cassettes_give_no_options_at_all()
    {
        var result = NoteSelector.Select(Notes((100, 0), (50, 0)), 100);

        Assert.Null(result.Exact);
        Assert.Null(result.Lower);
        Assert.Null(result.Upper);
    }

    [Fact]
    public void Amount_below_the_smallest_note_has_only_an_upper_alternative()
    {
        var result = NoteSelector.Select(Notes((50, 2), (20, 2)), 10);

        Assert.Null(result.Exact);
        Assert.Null(result.Lower);
        AssertBreakdown(result.Upper, 20, (20, 1));
    }

    [Fact]
    public void Amount_that_is_not_a_multiple_of_ten_falls_back_to_alternatives()
    {
        var result = NoteSelector.Select(Notes((50, 2), (20, 5)), 75);

        Assert.Null(result.Exact);
        AssertBreakdown(result.Lower, 70, (50, 1), (20, 1));
        AssertBreakdown(result.Upper, 80, (20, 4));
    }

    [Fact]
    public void Never_uses_more_notes_than_a_cassette_holds()
    {
        var result = NoteSelector.Select(Notes((500, 2), (100, 1), (50, 1), (20, 3)), 1210);

        AssertBreakdown(result.Exact, 1210, (500, 2), (100, 1), (50, 1), (20, 3));
    }

    [Fact]
    public void Full_atm_is_solved_quickly()
    {
        var result = NoteSelector.Select(Notes((500, 100), (100, 100), (50, 100), (20, 100)), 66_980);

        Assert.NotNull(result.Exact);
        Assert.Equal(66_980, result.Exact.Notes.Sum(n => n.Key * n.Value));
        Assert.All(result.Exact.Notes, n => Assert.InRange(n.Value, 0, 100));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-50)]
    public void Non_positive_amount_is_rejected(int amount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NoteSelector.Select(Notes((100, 1)), amount));
    }
}

public class CassetteAllocatorTests
{
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();

    [Fact]
    public void Dispenses_from_the_cassette_with_the_higher_fill_ratio_first()
    {
        var result = CassetteAllocator.Dispense(
            [new CassetteAllocator.Source(A, 1, 40, 100), new CassetteAllocator.Source(B, 2, 80, 100)], 30);

        Assert.Equal(30, result[B]);
        Assert.False(result.ContainsKey(A));
    }

    [Fact]
    public void Drains_evenly_once_the_cassettes_are_level()
    {
        var result = CassetteAllocator.Dispense(
            [new CassetteAllocator.Source(A, 1, 40, 100), new CassetteAllocator.Source(B, 2, 80, 100)], 50);

        // 40 notes bring B down to A's level, the remaining 10 are shared.
        Assert.Equal(45, result[B]);
        Assert.Equal(5, result[A]);
    }

    [Fact]
    public void Equal_fill_ratio_prefers_the_lower_slot()
    {
        var result = CassetteAllocator.Dispense(
            [new CassetteAllocator.Source(B, 2, 10, 100), new CassetteAllocator.Source(A, 1, 10, 100)], 1);

        Assert.Equal(1, result[A]);
    }

    [Fact]
    public void Deposit_fills_the_emptiest_matching_cassette_and_reports_the_overflow()
    {
        var (stored, overflow) = CassetteAllocator.Store(
            [new CassetteAllocator.Target(A, 1, 98, 100), new CassetteAllocator.Target(B, 2, 95, 100)], 10);

        Assert.Equal(5, stored[B]);
        Assert.Equal(2, stored[A]);
        Assert.Equal(3, overflow);
    }
}

public class AtmStateMachineTests
{
    [Theory]
    [InlineData(AtmState.Loaded, AtmState.InService)]
    [InlineData(AtmState.Loaded, AtmState.Maintenance)]
    [InlineData(AtmState.InService, AtmState.Maintenance)]
    [InlineData(AtmState.Maintenance, AtmState.InService)]
    public void Valid_transitions_are_accepted(AtmState from, AtmState to)
    {
        AtmStateMachine.EnsureTransition(from, to);
    }

    [Theory]
    [InlineData(AtmState.Unconfigured, AtmState.InService)]
    [InlineData(AtmState.Configured, AtmState.InService)]
    [InlineData(AtmState.Unconfigured, AtmState.Maintenance)]
    [InlineData(AtmState.InService, AtmState.Loaded)]
    [InlineData(AtmState.InService, AtmState.InService)]
    [InlineData(AtmState.Maintenance, AtmState.Unconfigured)]
    public void Invalid_transitions_are_rejected_with_a_meaningful_error(AtmState from, AtmState to)
    {
        var error = Assert.Throws<DomainException>(() => AtmStateMachine.EnsureTransition(from, to));

        Assert.Equal(ErrorCodes.InvalidStateTransition, error.Code);
        Assert.Contains(EnumText.Of(from), error.Message);
        Assert.Contains(EnumText.Of(to), error.Message);
    }

    [Theory]
    [InlineData(AtmOperation.Withdraw, AtmState.InService, true)]
    [InlineData(AtmOperation.Withdraw, AtmState.Maintenance, false)]
    [InlineData(AtmOperation.Withdraw, AtmState.Loaded, false)]
    [InlineData(AtmOperation.Deposit, AtmState.InService, true)]
    [InlineData(AtmOperation.Deposit, AtmState.Configured, false)]
    [InlineData(AtmOperation.ConfigureCassettes, AtmState.Unconfigured, true)]
    [InlineData(AtmOperation.ConfigureCassettes, AtmState.Maintenance, true)]
    [InlineData(AtmOperation.ConfigureCassettes, AtmState.InService, false)]
    [InlineData(AtmOperation.LoadCash, AtmState.Configured, true)]
    [InlineData(AtmOperation.LoadCash, AtmState.Maintenance, true)]
    [InlineData(AtmOperation.LoadCash, AtmState.InService, false)]
    [InlineData(AtmOperation.Reconcile, AtmState.Maintenance, true)]
    [InlineData(AtmOperation.Reconcile, AtmState.InService, false)]
    public void Operations_are_tied_to_states(AtmOperation operation, AtmState state, bool allowed)
    {
        Assert.Equal(allowed, AtmStateMachine.IsAllowed(state, operation));
    }
}

public class CassetteLayoutRulesTests
{
    private static List<CassetteSpec> ValidLayout() =>
    [
        new(1, CassetteType.Recycling, Currency.TRY, 100),
        new(2, CassetteType.Recycling, Currency.TRY, 50),
        new(3, CassetteType.Recycling, Currency.TRY, 20),
        new(4, CassetteType.Recycling, Currency.USD, 100),
        new(5, CassetteType.Deposit, null, null)
    ];

    [Fact]
    public void A_standard_layout_is_valid()
    {
        Assert.Empty(CassetteLayoutRules.Validate(ValidLayout()));
    }

    [Fact]
    public void The_same_currency_and_denomination_may_be_used_twice()
    {
        var layout = ValidLayout();
        layout[1] = new CassetteSpec(2, CassetteType.Recycling, Currency.TRY, 100);

        Assert.Empty(CassetteLayoutRules.Validate(layout));
    }

    [Fact]
    public void At_least_one_foreign_currency_cassette_is_required()
    {
        var layout = ValidLayout();
        layout[3] = new CassetteSpec(4, CassetteType.Recycling, Currency.TRY, 500);

        Assert.Single(CassetteLayoutRules.Validate(layout));
    }

    [Fact]
    public void More_than_two_foreign_currency_cassettes_are_rejected()
    {
        var layout = ValidLayout();
        layout[1] = new CassetteSpec(2, CassetteType.Recycling, Currency.EUR, 50);
        layout[2] = new CassetteSpec(3, CassetteType.Recycling, Currency.EUR, 20);

        Assert.Single(CassetteLayoutRules.Validate(layout));
    }

    [Fact]
    public void Unsupported_denominations_are_rejected()
    {
        var layout = ValidLayout();
        layout[0] = new CassetteSpec(1, CassetteType.Recycling, Currency.TRY, 200);

        Assert.Single(CassetteLayoutRules.Validate(layout));
    }

    [Fact]
    public void Exactly_one_deposit_cassette_is_required()
    {
        var layout = ValidLayout();
        layout[4] = new CassetteSpec(5, CassetteType.Recycling, Currency.TRY, 500);

        Assert.Single(CassetteLayoutRules.Validate(layout));
    }

    [Fact]
    public void Five_distinct_slots_are_required()
    {
        var layout = ValidLayout();
        layout[1] = new CassetteSpec(1, CassetteType.Recycling, Currency.TRY, 50);

        Assert.Single(CassetteLayoutRules.Validate(layout));
    }
}
