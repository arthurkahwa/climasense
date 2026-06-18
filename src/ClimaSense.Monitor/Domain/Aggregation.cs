namespace ClimaSense.Monitor.Domain;

public readonly record struct SeriesPoint(
    DateTime BucketStartCet,
    double AvgTemp, int MinTemp, int MaxTemp,
    double AvgHumidity, int MinHumidity, int MaxHumidity,
    int Count);

public readonly record struct DailyAggregate(
    DateOnly DateCet,
    double AvgTemp, int MinTemp, int MaxTemp,
    double AvgHumidity, int MinHumidity, int MaxHumidity,
    int Count);

/// <summary>A single un-aggregated reading, projected for the "actual values" chart mode.</summary>
public readonly record struct RawPoint(
    DateTime TimestampCet, int TemperatureC, int HumidityPct);

public static class BucketSelector
{
    public static int BucketMinutes(TimeSpan range) => range.TotalDays switch
    {
        <= 2    => 15,
        <= 14   => 60,
        <= 90   => 360,
        <= 730  => 1440,    // ≤ 2y -> 1 day
        <= 1825 => 10080,   // ≤ 5y -> 1 week
        _       => 43200,   // else -> ~30 days (monthly)
    };
}
