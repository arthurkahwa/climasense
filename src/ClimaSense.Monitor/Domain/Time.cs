namespace ClimaSense.Monitor.Domain;

public interface IClock { DateTime UtcNow { get; } }

public sealed class SystemClock : IClock { public DateTime UtcNow => DateTime.UtcNow; }

public static class CetZone
{
    // CET/CEST, resolved cross-platform. "Europe/Berlin" is the IANA id (macOS/Linux dev);
    // "W. Europe Standard Time" is the Windows registry id for the same zone. A (German)
    // Windows Server does not resolve the IANA id, so try both — same DST rules either way.
    public static readonly TimeZoneInfo Cet = ResolveCet();

    static TimeZoneInfo ResolveCet()
    {
        foreach (var id in new[] { "Europe/Berlin", "W. Europe Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        throw new TimeZoneNotFoundException(
            "Central European time zone not found (tried 'Europe/Berlin' and 'W. Europe Standard Time').");
    }

    public static DateTime ToUtc(DateTime cetWallClock)
    {
        var local = DateTime.SpecifyKind(cetWallClock, DateTimeKind.Unspecified);
        // Spring-forward gap: this wall-clock instant doesn't exist; nudge past it
        // by the (always 1h for Europe/Berlin) DST delta so conversion can't throw.
        if (Cet.IsInvalidTime(local)) local = local.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(local, Cet);
    }

    public static DateTime FromUtc(DateTime utc)
        => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Cet);
}

public static class Freshness
{
    public static bool IsStale(DateTime readingCet, DateTime nowUtc, int freshnessMinutes)
        => nowUtc - CetZone.ToUtc(readingCet) > TimeSpan.FromMinutes(freshnessMinutes);

    public static int MinutesOld(DateTime readingCet, DateTime nowUtc)
        => (int)Math.Max(0, (nowUtc - CetZone.ToUtc(readingCet)).TotalMinutes);
}
