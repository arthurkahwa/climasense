namespace ClimaSense.Monitor.Domain;

public enum AlertKind { Breach, StaleFeed, Anomaly, Sensor, Forecast, Condensation }

/// <summary>A live alert projection. EndCet == null means the breach is ongoing (active).</summary>
public readonly record struct Alert(
    AlertKind Kind, Metric? Metric, DateTime StartCet, DateTime? EndCet,
    ReadingBand Severity, string Message);

public static class AlertEvaluator
{
    const int ForecastWarnSteps = 12;            // ~3 h at the 15-min cadence
    const double CondensationMarginWarnC = 3.0;  // °C of headroom above the dew point

    public static IReadOnlyList<Alert> Evaluate(
        IReadOnlyList<SeriesPoint> series, int bucketMinutes,
        SensorReading? latest, DateTime nowUtc, EnvelopeOptions envelope)
    {
        var alerts = new List<Alert>();
        // A breach that reaches the end of the available data is still ongoing (active).
        DateTime? seriesEnd = series.Count > 0 ? series[^1].BucketStartCet.AddMinutes(bucketMinutes) : null;
        foreach (var (metric, recommended, allowable) in Metrics(envelope))
            foreach (var ex in ExcursionDetector.Detect(series, metric, bucketMinutes, recommended, allowable))
            {
                bool active = ex.EndCet == seriesEnd;
                DateTime? endCet = active ? null : ex.EndCet;
                string span = active ? $"seit {ex.StartCet:HH:mm} (laufend)" : $"{ex.StartCet:HH:mm}–{ex.EndCet:HH:mm}";
                alerts.Add(new Alert(AlertKind.Breach, metric, ex.StartCet, endCet, ex.Band, $"{De(metric)} {De(ex.Band)} {span}"));
            }

        if (latest is { } r && Freshness.IsStale(r.Timestamp, nowUtc, envelope.FreshnessMinutes))
            alerts.Add(new Alert(AlertKind.StaleFeed, null, r.Timestamp, null, ReadingBand.OutOfRange,
                $"Datenfeed veraltet seit {r.Timestamp:HH:mm} ({Freshness.MinutesOld(r.Timestamp, nowUtc)} Min)"));

        // Statistical anomalies that are still in-band (out-of-band points are already a Breach) — the early-warning signal.
        foreach (var (metric, recommended, allowable) in Metrics(envelope))
            foreach (var a in DriftDetector.Analyze(series, metric).Anomalies)
                if (BandEvaluator.Classify(a.Value, recommended, allowable) == ReadingBand.Recommended)
                    alerts.Add(new Alert(AlertKind.Anomaly, metric, a.BucketStartCet, a.BucketStartCet, ReadingBand.Allowable,
                        $"{De(metric)} Anomalie {a.Value:0.#} (z {a.Score:0.0}) um {a.BucketStartCet:HH:mm}"));

        // Sensor malfunction + forecast early-warning, anchored at the latest bucket. (Stuck-sensor is left to the
        // Insights tab — too easily a false positive in a steady room; only an implausible jump raises an alert.)
        if (series.Count > 0)
        {
            var anchor = series[^1].BucketStartCet;
            foreach (var (metric, recommended, _) in Metrics(envelope))
            {
                if (SensorHealth.Evaluate(series, metric) == SensorStatus.Spike)
                    alerts.Add(new Alert(AlertKind.Sensor, metric, anchor, null, ReadingBand.OutOfRange, $"{De(metric)} Sensor-Ausreißer"));

                if (Forecaster.StepsToThreshold(series, metric, recommended.Max) is { } n && n <= ForecastWarnSteps)
                    alerts.Add(new Alert(AlertKind.Forecast, metric, anchor, null, ReadingBand.Allowable,
                        $"{De(metric)} steigt auf {recommended.Max:0.#} in ~{n} Intervallen"));
            }
        }

        // Condensation risk from the latest reading (dew point close to the air temperature).
        if (latest is { } lr)
        {
            double margin = Psychrometrics.CondensationMarginC(lr.TemperatureC, lr.HumidityPct);
            if (margin < CondensationMarginWarnC)
                alerts.Add(new Alert(AlertKind.Condensation, null, lr.Timestamp, null,
                    margin < 1 ? ReadingBand.OutOfRange : ReadingBand.Allowable,
                    $"Kondensationsrisiko: {margin:0.#} °C über Taupunkt"));
        }

        return alerts.OrderByDescending(a => a.StartCet).ToList();
    }

    static IEnumerable<(Metric Metric, EnvelopeRange Recommended, EnvelopeRange Allowable)> Metrics(EnvelopeOptions e)
    {
        yield return (Metric.Temperature, e.TemperatureRecommended, e.TemperatureAllowable);
        yield return (Metric.Humidity, e.HumidityRecommended, e.HumidityAllowable);
    }

    static string De(Metric m) => m == Metric.Temperature ? "Temperatur" : "Feuchte";
    static string De(ReadingBand b) => b switch
    {
        ReadingBand.Recommended => "Normal",
        ReadingBand.Allowable => "Zulässig",
        _ => "Kritisch",
    };
}
