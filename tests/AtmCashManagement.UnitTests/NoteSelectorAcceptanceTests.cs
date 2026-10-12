using AtmCashManagement.Api.Domain;

namespace AtmCashManagement.UnitTests;

/// <summary>The eight acceptance tests of requirement B3, in the order of the assignment table.</summary>
public class NoteSelectorAcceptanceTests
{
    [Fact(DisplayName = "B3 #1: TRY 100x0, 50x3, 20x10, request 160 -> exact 50x2 + 20x3")]
    public void Test1_exact_match_that_greedy_misses()
    {
        var result = NoteSelector.Select(Notes((100, 0), (50, 3), (20, 10)), 160);

        AssertBreakdown(result.Exact, 160, (50, 2), (20, 3));
        Assert.Null(result.Lower);
        Assert.Null(result.Upper);
    }

    [Fact(DisplayName = "B3 #2: TRY 50x1, 20x2, request 80 -> lower 70, upper 90")]
    public void Test2_no_exact_match_gives_both_alternatives()
    {
        var result = NoteSelector.Select(Notes((50, 1), (20, 2)), 80);

        Assert.Null(result.Exact);
        AssertBreakdown(result.Lower, 70, (50, 1), (20, 1));
        AssertBreakdown(result.Upper, 90, (50, 1), (20, 2));
    }

    [Fact(DisplayName = "B3 #3: TRY 100x5, 20x10, request 130 -> lower 120, upper 140")]
    public void Test3_amount_not_reachable_with_available_denominations()
    {
        var result = NoteSelector.Select(Notes((100, 5), (20, 10)), 130);

        Assert.Null(result.Exact);
        AssertBreakdown(result.Lower, 120, (100, 1), (20, 1));
        AssertBreakdown(result.Upper, 140, (100, 1), (20, 2));
    }

    [Fact(DisplayName = "B3 #4: USD 100x2, request 250 -> lower 200, no upper")]
    public void Test4_no_upper_alternative_when_cash_runs_out()
    {
        var result = NoteSelector.Select(Notes((100, 2)), 250);

        Assert.Null(result.Exact);
        AssertBreakdown(result.Lower, 200, (100, 2));
        Assert.Null(result.Upper);
    }

    [Fact(DisplayName = "B3 #5: TRY 500x1, 100x0, 20x5, request 600 -> exact 500x1 + 20x5")]
    public void Test5_skips_an_empty_denomination()
    {
        var result = NoteSelector.Select(Notes((500, 1), (100, 0), (20, 5)), 600);

        AssertBreakdown(result.Exact, 600, (500, 1), (20, 5));
    }

    [Fact(DisplayName = "B3 #6: TRY 100x1, 50x4, 20x5, request 200 -> exact 100x1 + 50x2 (fewest notes)")]
    public void Test6_picks_the_breakdown_with_the_fewest_notes()
    {
        var result = NoteSelector.Select(Notes((100, 1), (50, 4), (20, 5)), 200);

        AssertBreakdown(result.Exact, 200, (100, 1), (50, 2));
        Assert.Equal(3, result.Exact!.NoteCount);
    }

    [Fact(DisplayName = "B3 #7: TRY 500x1, 100x3, 20x20, request 1000 -> exact 500x1 + 100x3 + 20x10")]
    public void Test7_uses_small_notes_when_large_ones_run_out()
    {
        var result = NoteSelector.Select(Notes((500, 1), (100, 3), (20, 20)), 1000);

        AssertBreakdown(result.Exact, 1000, (500, 1), (100, 3), (20, 10));
    }

    [Fact(DisplayName = "B3 #8: TRY 100x3, 50x1, 20x1, request 110 -> lower 100, upper 120")]
    public void Test8_alternatives_use_the_fewest_notes_too()
    {
        var result = NoteSelector.Select(Notes((100, 3), (50, 1), (20, 1)), 110);

        Assert.Null(result.Exact);
        AssertBreakdown(result.Lower, 100, (100, 1));
        AssertBreakdown(result.Upper, 120, (100, 1), (20, 1));
    }

    internal static Dictionary<int, int> Notes(params (int Denomination, int Count)[] notes) =>
        notes.ToDictionary(n => n.Denomination, n => n.Count);

    internal static void AssertBreakdown(NoteBreakdown? actual, int expectedAmount,
        params (int Denomination, int Count)[] expectedNotes)
    {
        Assert.NotNull(actual);
        Assert.Equal(expectedAmount, actual.Amount);
        Assert.Equal(
            expectedNotes.OrderBy(n => n.Denomination),
            actual.Notes.Select(n => (n.Key, n.Value)).OrderBy(n => n.Key));
    }
}
