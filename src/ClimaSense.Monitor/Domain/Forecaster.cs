namespace ClimaSense.Monitor.Domain;

public static class Forecaster
{
    // Fit least-squares line y = intercept + slope*x, x = 0..n-1.
    // Forecast: lastFitted + slope*k for k = 1..steps, where lastFitted = intercept + slope*(n-1).
    public static IReadOnlyList<double> Forecast(
        IReadOnlyList<SeriesPoint> series, Metric metric, int steps)
    {
        if (series.Count == 0 || steps <= 0)
            return [];

        var (slope, intercept) = LeastSquares(series, metric);
        int n = series.Count;
        double lastFitted = intercept + slope * (n - 1);

        var result = new double[steps];
        for (int k = 1; k <= steps; k++)
            result[k - 1] = lastFitted + slope * k;
        return result;
    }

    // StepsToThreshold: steps until trend reaches `limit` in slope's direction.
    //   slope > 0 && limit > lastFitted => ceil((limit - lastFitted)/slope)
    //   slope < 0 && limit < lastFitted => ceil((lastFitted - limit)/(-slope))
    //   otherwise => null
    public static int? StepsToThreshold(
        IReadOnlyList<SeriesPoint> series, Metric metric, double limit)
    {
        if (series.Count < 2)
            return null;

        var (slope, intercept) = LeastSquares(series, metric);
        if (slope == 0)
            return null;

        int n = series.Count;
        double lastFitted = intercept + slope * (n - 1);

        if (slope > 0 && limit > lastFitted)
            return (int)Math.Ceiling((limit - lastFitted) / slope);
        if (slope < 0 && limit < lastFitted)
            return (int)Math.Ceiling((lastFitted - limit) / (-slope));

        return null;
    }

    static (double slope, double intercept) LeastSquares(
        IReadOnlyList<SeriesPoint> series, Metric metric)
    {
        int n = series.Count;
        double sumX = 0, sumY = 0, sumXX = 0, sumXY = 0;

        for (int i = 0; i < n; i++)
        {
            double x = i;
            double y = metric == Metric.Temperature
                ? series[i].AvgTemp
                : series[i].AvgHumidity;
            sumX  += x;
            sumY  += y;
            sumXX += x * x;
            sumXY += x * y;
        }

        double denom = n * sumXX - sumX * sumX;
        if (denom == 0) return (0, sumY / n);

        double slope     = (n * sumXY - sumX * sumY) / denom;
        double intercept = (sumY - slope * sumX) / n;
        return (slope, intercept);
    }
}
