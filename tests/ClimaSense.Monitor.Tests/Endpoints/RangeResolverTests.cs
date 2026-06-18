using System;
using ClimaSense.Monitor.Endpoints;
using Xunit;

namespace ClimaSense.Monitor.Tests.Endpoints;

public class RangeResolverTests
{
    static readonly DateTime NowCet = new(2026, 6, 15, 18, 0, 0);

    [Fact]
    public void Preset_24h_resolves_window()
    {
        Assert.True(RangeResolver.TryResolve("24h", null, null, NowCet, out var f, out var t, out var err));
        Assert.Null(err);
        Assert.Equal(NowCet, t);
        Assert.Equal(NowCet.AddHours(-24), f);
    }

    [Fact]
    public void Unknown_preset_is_rejected()
        => Assert.False(RangeResolver.TryResolve("3y", null, null, NowCet, out _, out _, out _));

    [Fact]
    public void From_after_to_is_rejected()
        => Assert.False(RangeResolver.TryResolve(null, NowCet, NowCet.AddDays(-1), NowCet, out _, out _, out _));

    [Fact]
    public void Custom_window_is_accepted()
    {
        Assert.True(RangeResolver.TryResolve(null, NowCet.AddDays(-3), NowCet, NowCet, out var f, out var t, out _));
        Assert.Equal(NowCet.AddDays(-3), f);
        Assert.Equal(NowCet, t);
    }

    [Fact]
    public void Preset_5y_resolves_window()
    {
        Assert.True(RangeResolver.TryResolve("5y", null, null, NowCet, out var f, out var t, out _));
        Assert.Equal(NowCet, t);
        Assert.Equal(NowCet.AddDays(-1825), f);
    }

    [Fact]
    public void Preset_all_resolves_a_long_window_back()
    {
        Assert.True(RangeResolver.TryResolve("all", null, null, NowCet, out var f, out var t, out _));
        Assert.Equal(NowCet, t);
        Assert.True(f < new DateTime(2010, 1, 1)); // covers the full 2016-> dataset
    }
}
