namespace ClimaSense.Monitor.Domain;

public enum SensorStatus { Healthy, Stuck, Spike }

public static class SensorHealth
{
    /// <summary>
    /// Evaluates the health of a sensor time series by detecting spikes and stuck (flatlined) readings.
    ///
    /// Precedence: Spike beats Stuck beats Healthy.
    ///   - Spike: any consecutive pair where |v[i] - v[i-1]| > <paramref name="maxStep"/>.
    ///   - Stuck: series has at least <paramref name="stuckRun"/> points AND the last
    ///     <paramref name="stuckRun"/> values are all identical.
    ///     NOTE: This is heuristic — a genuinely very stable room can flatline on integer
    ///     readings, so Stuck is informational, not a hard fault.
    ///   - Healthy: everything else, including series with fewer than 2 points.
    /// </summary>
    public static SensorStatus Evaluate(
        IReadOnlyList<SeriesPoint> series,
        Metric metric,
        int stuckRun = 8,
        double maxStep = 10)
    {
        if (series.Count < 2)
            return SensorStatus.Healthy;

        var values = series.Select(p => metric == Metric.Temperature ? p.AvgTemp : p.AvgHumidity).ToArray();

        // Spike: any consecutive step exceeds maxStep
        for (int i = 1; i < values.Length; i++)
        {
            if (Math.Abs(values[i] - values[i - 1]) > maxStep)
                return SensorStatus.Spike;
        }

        // Stuck: last stuckRun values all identical
        if (values.Length >= stuckRun)
        {
            double tail = values[^1];
            bool flatlined = values[^stuckRun..].All(v => v == tail);
            if (flatlined)
                return SensorStatus.Stuck;
        }

        return SensorStatus.Healthy;
    }
}
