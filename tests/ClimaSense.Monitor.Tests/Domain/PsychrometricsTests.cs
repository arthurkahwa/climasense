using ClimaSense.Monitor.Domain;
using Xunit;

namespace ClimaSense.Monitor.Tests.Domain;

public class PsychrometricsTests
{
    // Cycle 1: Tracer — known dew point
    [Fact]
    public void DewPoint_at_20C_50pct_is_approximately_9_3C()
        => Assert.Equal(9.26, Psychrometrics.DewPointC(20, 50), 1);

    // Cycle 2: Saturated air — dew point equals air temperature
    [Fact]
    public void DewPoint_at_saturation_equals_air_temperature()
        => Assert.Equal(20.0, Psychrometrics.DewPointC(20, 100), 1);

    // Cycle 3: Condensation margin at saturation is approximately zero
    [Fact]
    public void CondensationMargin_at_saturation_is_approximately_zero()
        => Assert.Equal(0.0, Psychrometrics.CondensationMarginC(20, 100), 1);

    // Cycle 4: Drier air has a larger condensation margin than humid air
    [Fact]
    public void CondensationMargin_is_larger_for_drier_air()
        => Assert.True(Psychrometrics.CondensationMarginC(20, 30) > Psychrometrics.CondensationMarginC(20, 70));
}
