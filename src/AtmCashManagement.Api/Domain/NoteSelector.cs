namespace AtmCashManagement.Api.Domain;

public sealed record NoteBreakdown(int Amount, IReadOnlyDictionary<int, int> Notes)
{
    public int NoteCount => Notes.Values.Sum();
}

/// <summary>
/// Either <see cref="Exact"/> is set, or the request cannot be paid exactly and
/// <see cref="Lower"/> / <see cref="Upper"/> hold the nearest payable amounts (each may be null).
/// </summary>
public sealed record NoteSelectionResult(NoteBreakdown? Exact, NoteBreakdown? Lower, NoteBreakdown? Upper)
{
    public static readonly NoteSelectionResult None = new(null, null, null);
}

/// <summary>
/// Finds the breakdown with the fewest notes for a limited supply of each denomination.
///
/// A greedy "largest note first" walk is wrong here: with 50x3 and 20x10 it takes three 50s for
/// a request of 160 and is then stuck 10 short, although 50x2 + 20x3 pays it exactly.
/// The problem is a bounded knapsack, solved with dynamic programming over every reachable amount:
/// minNotes[a] = fewest notes that add up to amount a. The same table also answers the
/// "closest payable amount" question, because it knows every amount that can be paid.
///
/// Amounts are divided by the greatest common divisor of the denominations (10 for 20/50/100/500),
/// so a full ATM (4 cassettes x 100 notes x 500) needs at most 20,001 table cells.
/// </summary>
public static class NoteSelector
{
    private const int Unreachable = int.MaxValue;

    /// <param name="availableNotes">Denomination -> number of notes that may be used.</param>
    /// <param name="requestedAmount">Amount the customer asked for.</param>
    /// <param name="maxUpperAmount">
    /// Highest amount the upper alternative may have (the customer's remaining daily limit).
    /// Null means no limit.
    /// </param>
    public static NoteSelectionResult Select(IReadOnlyDictionary<int, int> availableNotes, int requestedAmount,
        int? maxUpperAmount = null)
    {
        if (requestedAmount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedAmount), "Amount must be positive.");
        }

        // Ascending order matters for the tie-break, see below.
        var stock = availableNotes
            .Where(kv => kv.Key > 0 && kv.Value > 0)
            .OrderBy(kv => kv.Key)
            .Select(kv => (Denomination: kv.Key, Count: kv.Value))
            .ToArray();

        if (stock.Length == 0)
        {
            return NoteSelectionResult.None;
        }

        var unit = stock.Select(s => s.Denomination).Aggregate(Gcd);
        var total = stock.Sum(s => (long)s.Denomination * s.Count);

        // No point in looking past the cash in the machine, or past what the customer may take.
        var ceiling = maxUpperAmount is null
            ? total
            : Math.Min(total, Math.Max(requestedAmount, maxUpperAmount.Value));
        var size = (int)(ceiling / unit) + 1;

        var minNotes = new int[size];
        Array.Fill(minNotes, Unreachable);
        minNotes[0] = 0;

        // taken[i][a] = how many notes of denomination i the best solution for amount a uses,
        // considering denominations 0..i only. Used to rebuild the breakdown.
        var taken = new int[stock.Length][];

        for (var i = 0; i < stock.Length; i++)
        {
            var step = stock[i].Denomination / unit;
            var limit = stock[i].Count;
            var next = new int[size];
            var take = new int[size];

            for (var amount = 0; amount < size; amount++)
            {
                var best = Unreachable;
                var bestK = 0;
                for (var k = 0; k <= limit && k * step <= amount; k++)
                {
                    var previous = minNotes[amount - k * step];
                    if (previous == Unreachable)
                    {
                        continue;
                    }

                    // "<=" keeps the larger k on a tie. Because denominations are processed in
                    // ascending order, two breakdowns with the same note count resolve in favour
                    // of the one using more high-value notes, which keeps the result deterministic.
                    if (previous + k <= best)
                    {
                        best = previous + k;
                        bestK = k;
                    }
                }

                next[amount] = best;
                take[amount] = bestK;
            }

            minNotes = next;
            taken[i] = take;
        }

        if (requestedAmount % unit == 0)
        {
            var index = requestedAmount / unit;
            if (index < size && minNotes[index] != Unreachable)
            {
                return new NoteSelectionResult(Rebuild(index), null, null);
            }
        }

        NoteBreakdown? lower = null;
        for (var index = Math.Min(size - 1, (requestedAmount - 1) / unit); index >= 1; index--)
        {
            if (minNotes[index] != Unreachable)
            {
                lower = Rebuild(index);
                break;
            }
        }

        NoteBreakdown? upper = null;
        for (var index = requestedAmount / unit + 1; index < size; index++)
        {
            if (minNotes[index] != Unreachable)
            {
                upper = Rebuild(index);
                break;
            }
        }

        return new NoteSelectionResult(null, lower, upper);

        NoteBreakdown Rebuild(int index)
        {
            var notes = new Dictionary<int, int>();
            var remaining = index;
            for (var i = stock.Length - 1; i >= 0; i--)
            {
                var k = taken[i][remaining];
                if (k > 0)
                {
                    notes[stock[i].Denomination] = k;
                    remaining -= k * (stock[i].Denomination / unit);
                }
            }

            return new NoteBreakdown(index * unit, notes);
        }
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a;
    }
}
