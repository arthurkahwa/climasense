using System;
using ClimaSense.Web.Clock;
using ClimaSense.Web.Cursor;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ClimaSense.Web.Tests;

/// <summary>
/// Unit tests covering the three slice-1 acceptance criteria for
/// <see cref="CursorSnapshot"/> in the .NET tier:
///
///   AC9 — scope-singleton (same as_of within one DI scope).
///   AC10 — windowed(24h) returns a tuple where end-start == 24h.
///   AC11 — should_emit returns true iff as_of - last >= cadence.
///
/// Plus a small set of guard-rail assertions that lock the
/// CONTEXT.md-described semantics (immutability, UTC normalisation,
/// clip() targeting raw SensorReadings only).
/// </summary>
public sealed class CursorSnapshotTests
{
    private sealed class FixedClock : IClock
    {
        public DateTime Value { get; set; } = new(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        public DateTime UtcNow() => Value;
    }

    // -----------------------------------------------------------------
    // AC9: scope-singleton.
    // -----------------------------------------------------------------
    [Fact]
    public void Scoped_resolution_within_same_scope_returns_same_AsOf()
    {
        var clock = new FixedClock();
        var services = new ServiceCollection()
            .AddSingleton<IClock>(clock)
            .AddScoped<CursorSnapshot>(sp =>
                CursorSnapshot.FromClock(sp.GetRequiredService<IClock>()))
            .BuildServiceProvider();

        using var scope = services.CreateScope();

        var first = scope.ServiceProvider.GetRequiredService<CursorSnapshot>();

        // Mutate the clock between resolves: a fresh snapshot would now
        // see the new value. Scope-singleton means the second resolve
        // returns the same instance with the original value.
        clock.Value = clock.Value.AddHours(7);

        var second = scope.ServiceProvider.GetRequiredService<CursorSnapshot>();

        Assert.Same(first, second);
        Assert.Equal(first.AsOf, second.AsOf);
        Assert.NotEqual(clock.Value, second.AsOf);
    }

    [Fact]
    public void Different_scopes_get_distinct_snapshots()
    {
        var clock = new FixedClock();
        var services = new ServiceCollection()
            .AddSingleton<IClock>(clock)
            .AddScoped<CursorSnapshot>(sp =>
                CursorSnapshot.FromClock(sp.GetRequiredService<IClock>()))
            .BuildServiceProvider();

        CursorSnapshot a;
        using (var scopeA = services.CreateScope())
        {
            a = scopeA.ServiceProvider.GetRequiredService<CursorSnapshot>();
        }

        clock.Value = clock.Value.AddDays(1);

        CursorSnapshot b;
        using (var scopeB = services.CreateScope())
        {
            b = scopeB.ServiceProvider.GetRequiredService<CursorSnapshot>();
        }

        Assert.NotSame(a, b);
        Assert.NotEqual(a.AsOf, b.AsOf);
    }

    // -----------------------------------------------------------------
    // AC10: Windowed.
    // -----------------------------------------------------------------
    [Fact]
    public void Windowed_24h_yields_24h_window_ending_at_AsOf()
    {
        var asOf = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var snap = new CursorSnapshot(asOf);

        var (start, end) = snap.Windowed(TimeSpan.FromHours(24));

        Assert.Equal(asOf, end);
        Assert.Equal(TimeSpan.FromHours(24), end - start);
    }

    [Fact]
    public void Windowed_rejects_non_positive_duration()
    {
        var snap = new CursorSnapshot(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Throws<ArgumentOutOfRangeException>(() => snap.Windowed(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => snap.Windowed(TimeSpan.FromSeconds(-1)));
    }

    // -----------------------------------------------------------------
    // AC11: should_emit.
    // -----------------------------------------------------------------
    [Theory]
    [InlineData(0, false)]                  // gap exactly zero — under cadence.
    [InlineData(59 * 60, false)]            // 59 minutes — under cadence.
    [InlineData(60 * 60, true)]             // exactly 1 hour — gate opens.
    [InlineData(2 * 60 * 60, true)]         // 2 hours — gate open.
    public void ShouldEmit_opens_iff_gap_meets_cadence(int gapSeconds, bool expected)
    {
        var asOf = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var last = asOf - TimeSpan.FromSeconds(gapSeconds);
        var snap = new CursorSnapshot(asOf);

        Assert.Equal(expected, snap.ShouldEmit(last, TimeSpan.FromHours(1)));
    }

    [Fact]
    public void ShouldEmit_with_null_last_emit_always_opens()
    {
        var snap = new CursorSnapshot(new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc));
        Assert.True(snap.ShouldEmit(null, TimeSpan.FromHours(1)));
        Assert.True(snap.ShouldEmit(null, TimeSpan.FromMilliseconds(1)));
    }

    [Fact]
    public void ShouldEmit_rejects_non_positive_cadence()
    {
        var snap = new CursorSnapshot(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Throws<ArgumentOutOfRangeException>(() => snap.ShouldEmit(snap.AsOf, TimeSpan.Zero));
    }

    // -----------------------------------------------------------------
    // Guard rails: UTC normalisation, clip targeting SensorReadings.
    // -----------------------------------------------------------------
    [Fact]
    public void Construction_normalises_unspecified_kind_to_utc()
    {
        var raw = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Unspecified);
        var snap = new CursorSnapshot(raw);
        Assert.Equal(DateTimeKind.Utc, snap.AsOf.Kind);
        Assert.Equal(raw.Ticks, snap.AsOf.Ticks);
    }

    [Fact]
    public void Clip_appends_WHERE_clause_when_query_has_none()
    {
        var snap = new CursorSnapshot(new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc));
        var (q, ts) = snap.Clip("SELECT TOP 100 * FROM SensorReadings");

        Assert.Contains("WHERE ReadingTime <= @asOf", q, StringComparison.Ordinal);
        Assert.Equal(snap.AsOf, ts);
    }

    [Fact]
    public void Clip_appends_AND_when_query_already_has_WHERE()
    {
        var snap = new CursorSnapshot(new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc));
        var (q, _) = snap.Clip("SELECT * FROM SensorReadings WHERE Temperature > 20");

        Assert.Contains("WHERE Temperature > 20 AND ReadingTime <= @asOf", q, StringComparison.Ordinal);
    }
}
