namespace ClimaSense.Monitor.Endpoints;

public static class RangeResolver
{
    static readonly Dictionary<string, TimeSpan> Presets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["24h"] = TimeSpan.FromHours(24),
        ["7d"]  = TimeSpan.FromDays(7),
        ["30d"] = TimeSpan.FromDays(30),
        ["90d"] = TimeSpan.FromDays(90),
        ["1y"]  = TimeSpan.FromDays(365),
        ["2y"]  = TimeSpan.FromDays(730),
        ["5y"]  = TimeSpan.FromDays(1825),
        ["all"] = TimeSpan.FromDays(365 * 30),  // covers the full 2016-> dataset
    };

    public static bool TryResolve(string? range, DateTime? from, DateTime? to, DateTime nowCet,
        out DateTime fromCet, out DateTime toCet, out string? error)
    {
        error = null; fromCet = default; toCet = default;
        if (!string.IsNullOrEmpty(range))
        {
            if (!Presets.TryGetValue(range, out var span)) { error = $"unknown range '{range}'"; return false; }
            toCet = nowCet; fromCet = nowCet - span; return true;
        }
        if (from is null || to is null) { error = "provide 'range' or both 'from' and 'to'"; return false; }
        if (from >= to) { error = "'from' must be before 'to'"; return false; }
        if (to.Value - from.Value > TimeSpan.FromDays(366 * 2)) { error = "range exceeds 2 years"; return false; }
        fromCet = from.Value; toCet = to.Value; return true;
    }
}
