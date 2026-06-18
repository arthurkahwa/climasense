using ClimaSense.Monitor.Domain;
using Xunit;

namespace ClimaSense.Monitor.Tests.Domain;

public class EnvelopeOptionsTests
{
    [Fact]
    public void Defaults_match_ASHRAE_TC99()
    {
        var e = new EnvelopeOptions();
        Assert.Equal(18, e.TemperatureRecommended.Min);
        Assert.Equal(27, e.TemperatureRecommended.Max);
        Assert.Equal(15, e.TemperatureAllowable.Min);
        Assert.Equal(32, e.TemperatureAllowable.Max);
        Assert.Equal(20, e.HumidityRecommended.Min);
        Assert.Equal(80, e.HumidityRecommended.Max);
        Assert.Equal(30, e.FreshnessMinutes);
    }
}
