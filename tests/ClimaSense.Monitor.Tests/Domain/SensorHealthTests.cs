using ClimaSense.Monitor.Domain;
using Xunit;

namespace ClimaSense.Monitor.Tests.Domain;

public class SensorHealthTests
{
    static SeriesPoint P(int hour, double temp, double hum = 50) =>
        new(new DateTime(2026, 6, 15, hour, 0, 0),
            temp, (int)temp, (int)temp,
            hum, (int)hum, (int)hum,
            4);

    [Fact]
    public void Normally_varying_series_is_healthy()
    {
        var temps = new double[] { 18, 19, 18, 19, 20, 19, 18, 19 };
        var series = temps.Select((t, i) => P(i, t)).ToList();

        var result = SensorHealth.Evaluate(series, Metric.Temperature);

        Assert.Equal(SensorStatus.Healthy, result);
    }

    [Fact]
    public void Large_single_step_jump_is_a_spike()
    {
        // |45 - 18| = 27 > 10 (default maxStep)
        var temps = new double[] { 18, 19, 18, 45, 19, 18 };
        var series = temps.Select((t, i) => P(i, t)).ToList();

        var result = SensorHealth.Evaluate(series, Metric.Temperature);

        Assert.Equal(SensorStatus.Spike, result);
    }

    [Fact]
    public void Last_four_identical_values_is_stuck()
    {
        // last 4 are all 20 — flatlined tail with stuckRun: 4
        var temps = new double[] { 18, 19, 20, 20, 20, 20 };
        var series = temps.Select((t, i) => P(i, t)).ToList();

        var result = SensorHealth.Evaluate(series, Metric.Temperature, stuckRun: 4);

        Assert.Equal(SensorStatus.Stuck, result);
    }

    [Fact]
    public void Single_point_series_is_healthy()
    {
        var series = new List<SeriesPoint> { P(0, 20) };

        var result = SensorHealth.Evaluate(series, Metric.Temperature);

        Assert.Equal(SensorStatus.Healthy, result);
    }
}
