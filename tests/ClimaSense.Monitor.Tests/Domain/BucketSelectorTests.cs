using System;
using ClimaSense.Monitor.Domain;
using Xunit;

namespace ClimaSense.Monitor.Tests.Domain;

public class BucketSelectorTests
{
    [Theory]
    [InlineData(1, 15)]
    [InlineData(2, 15)]
    [InlineData(3, 60)]
    [InlineData(14, 60)]
    [InlineData(30, 360)]
    [InlineData(90, 360)]
    [InlineData(365, 1440)]
    [InlineData(730, 1440)]    // 2y -> 1 day
    [InlineData(1825, 10080)]  // 5y -> 1 week
    [InlineData(3650, 43200)]  // ~10y / all -> ~1 month
    public void BucketMinutes_scales_with_range(double days, int expected)
        => Assert.Equal(expected, BucketSelector.BucketMinutes(TimeSpan.FromDays(days)));
}
