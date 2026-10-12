namespace AtmCashManagement.Api.Domain;

/// <summary>
/// Decides which physical cassette each note comes from, or goes to, when two recycling
/// cassettes hold the same currency and denomination.
/// </summary>
public static class CassetteAllocator
{
    public sealed record Source(Guid CassetteId, int Slot, int Available, int Capacity);

    public sealed record Target(Guid CassetteId, int Slot, int Count, int Capacity);

    /// <summary>
    /// Takes notes from the cassette with the higher fill ratio first. The ratio is re-evaluated
    /// after every note, so once two cassettes are level they are drained evenly. Ties go to the
    /// lower slot number.
    /// </summary>
    public static IReadOnlyDictionary<Guid, int> Dispense(IReadOnlyCollection<Source> sources, int noteCount)
    {
        if (sources.Sum(s => s.Available) < noteCount)
        {
            throw new InvalidOperationException("Not enough notes in the cassettes for the requested breakdown.");
        }

        var remaining = sources.ToDictionary(s => s.CassetteId, s => s.Available);
        var result = new Dictionary<Guid, int>();

        for (var i = 0; i < noteCount; i++)
        {
            var fullest = sources
                .Where(s => remaining[s.CassetteId] > 0)
                .OrderByDescending(s => (double)remaining[s.CassetteId] / s.Capacity)
                .ThenBy(s => s.Slot)
                .First();

            remaining[fullest.CassetteId]--;
            result[fullest.CassetteId] = result.GetValueOrDefault(fullest.CassetteId) + 1;
        }

        return result;
    }

    /// <summary>
    /// Puts deposited notes into the emptiest matching cassette first. Returns what was stored
    /// per cassette and how many notes did not fit.
    /// </summary>
    public static (IReadOnlyDictionary<Guid, int> Stored, int Overflow) Store(IReadOnlyCollection<Target> targets,
        int noteCount)
    {
        var stored = new Dictionary<Guid, int>();
        var left = noteCount;

        foreach (var target in targets.OrderBy(t => (double)t.Count / t.Capacity).ThenBy(t => t.Slot))
        {
            if (left == 0)
            {
                break;
            }

            var fits = Math.Min(left, target.Capacity - target.Count);
            if (fits > 0)
            {
                stored[target.CassetteId] = fits;
                left -= fits;
            }
        }

        return (stored, left);
    }
}
