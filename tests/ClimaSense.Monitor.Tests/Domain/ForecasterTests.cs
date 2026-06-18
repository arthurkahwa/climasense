using ClimaSense.Monitor.Domain;
using Xunit;

namespace ClimaSense.Monitor.Tests.Domain;

public class ForecasterTests
{
    static SeriesPoint P(int hour, double temp, double hum = 50) =>
        new(new DateTime(2026, 6, 15, hour, 0, 0),
            temp, (int)temp, (int)temp,
            hum, (int)hum, (int)hum,
            4);

    [Fact]
    public void Linear_series_forecasts_next_two_steps()
    {
        var series = new[] { P(0, 10), P(1, 11), P(2, 12), P(3, 13) };

        var result = Forecaster.Forecast(series, Metric.Temperature, steps: 2);

        Assert.Equal(2, result.Count);
        Assert.Equal(14.0, result[0], 3);
        Assert.Equal(15.0, result[1], 3);
    }

    [Fact]
    public void Flat_series_forecasts_constant_value()
    {
        var series = new[] { P(0, 20), P(1, 20), P(2, 20), P(3, 20) };

        var result = Forecaster.Forecast(series, Metric.Temperature, steps: 3);

        Assert.Equal(3, result.Count);
        Assert.Equal(20.0, result[0], 3);
        Assert.Equal(20.0, result[1], 3);
        Assert.Equal(20.0, result[2], 3);
    }

    [Fact]
    public void Rising_series_reaches_threshold_in_expected_steps()
    {
        // slope=1, intercept=10, lastFitted=13; limit=16 => ceil((16-13)/1)=3
        var series = new[] { P(0, 10), P(1, 11), P(2, 12), P(3, 13) };

        var result = Forecaster.StepsToThreshold(series, Metric.Temperature, limit: 16);

        Assert.Equal(3, result);
    }

    [Fact]
    public void Flat_series_returns_null_for_threshold()
    {
        // slope=0, threshold unreachable
        var series = new[] { P(0, 20), P(1, 20), P(2, 20), P(3, 20) };

        var result = Forecaster.StepsToThreshold(series, Metric.Temperature, limit: 30);

        Assert.Null(result);
    }
}
