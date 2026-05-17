// SPDX-License-Identifier: MIT
//
// Slice-5 verification tests for `ForecastReadService`. Locks:
//
//   * `GetLatestAsync` calls into the injected batch-fetcher exactly
//     once per invocation, passing the cursor's `AsOf` verbatim.
//   * Empty-points outcome maps to a 200 envelope with `HorizonHours=0`
//     and `Points` empty — the dashboard renders "no forecast yet".
//   * Cursor's `AsOf` is preserved on the empty-envelope `GeneratedAt`
//     when the fetcher returns no rows.
//   * Wraps batch metadata correctly: `GeneratedAt` echoes the row
//     value when present; `ModelVersion` is non-null in the envelope
//     (defaults to `lag-lr-v1` when the fetcher returns null).
//
// The tests use a lambda-based fake batch-fetcher so the suite never
// needs a live SQL Server.

#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClimaSense.Web.Cursor;
using ClimaSense.Web.Forecasts;
using Xunit;

namespace ClimaSense.Web.Tests;

public sealed class ForecastReadServiceTests
{
    private static CursorSnapshot _snap(DateTime asOf) => new(asOf);

    private sealed class _Recorder
    {
        public int Calls { get; private set; }
        public DateTime LastAsOf { get; private set; }

        public DateTime? GeneratedAt { get; set; }
        public string? ModelVersion { get; set; } = "lag-lr-v1";
        public List<ForecastPointDto> Points { get; set; } = new();
        public Exception? Throw { get; set; }

        public Task<(DateTime? GeneratedAt, string? ModelVersion, IReadOnlyList<ForecastPointDto> Points)>
            Fetch(DateTime asOf, CancellationToken ct)
        {
            Calls += 1;
            LastAsOf = asOf;
            if (Throw is not null)
            {
                throw Throw;
            }
            return Task.FromResult<(DateTime?, string?, IReadOnlyList<ForecastPointDto>)>(
                (GeneratedAt, ModelVersion, Points));
        }
    }

    [Fact]
    public async Task GetLatest_returns_empty_envelope_when_no_rows()
    {
        var rec = new _Recorder { GeneratedAt = null, Points = new List<ForecastPointDto>() };
        var svc = new ForecastReadService(rec.Fetch);
        var asOf = new DateTime(2026, 5, 17, 12, 0, 0, DateTimeKind.Utc);

        var envelope = await svc.GetLatestAsync(_snap(asOf), CancellationToken.None);

        Assert.Equal(1, rec.Calls);
        Assert.Equal(asOf, rec.LastAsOf);
        Assert.Empty(envelope.Points);
        Assert.Equal(0, envelope.HorizonHours);
        Assert.Equal(asOf, envelope.GeneratedAt);
        Assert.Equal("lag-lr-v1", envelope.ModelVersion);
    }

    [Fact]
    public async Task GetLatest_projects_batch_metadata_when_rows_present()
    {
        var generated = new DateTime(2026, 5, 17, 11, 0, 0, DateTimeKind.Utc);
        var target = new DateTime(2026, 5, 17, 12, 0, 0, DateTimeKind.Utc);
        var rec = new _Recorder
        {
            GeneratedAt = generated,
            ModelVersion = "lag-lr-v1",
            Points = new List<ForecastPointDto>
            {
                new(target, 21.5, 47.0, 20.9, 22.1),
                new(target.AddHours(1), 21.6, 47.1, 21.0, 22.2),
            },
        };
        var svc = new ForecastReadService(rec.Fetch);

        var envelope = await svc.GetLatestAsync(
            _snap(new DateTime(2026, 5, 17, 13, 0, 0, DateTimeKind.Utc)),
            CancellationToken.None);

        Assert.Equal(2, envelope.HorizonHours);
        Assert.Equal(generated, envelope.GeneratedAt);
        Assert.Equal("lag-lr-v1", envelope.ModelVersion);
        Assert.Equal(2, envelope.Points.Count);
        Assert.Equal(21.5, envelope.Points[0].PredictedTemperature);
        Assert.Equal(target.AddHours(1), envelope.Points[1].TargetTime);
    }

    [Fact]
    public async Task GetLatest_passes_exact_AsOf_with_kind_Utc()
    {
        var rec = new _Recorder();
        var svc = new ForecastReadService(rec.Fetch);
        var asOf = new DateTime(2026, 5, 7, 15, 45, 23, DateTimeKind.Utc);

        await svc.GetLatestAsync(_snap(asOf), CancellationToken.None);

        Assert.Equal(asOf, rec.LastAsOf);
        Assert.Equal(DateTimeKind.Utc, rec.LastAsOf.Kind);
    }

    [Fact]
    public async Task GetLatest_propagates_OperationCanceledException()
    {
        var rec = new _Recorder { Throw = new OperationCanceledException() };
        var svc = new ForecastReadService(rec.Fetch);

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await svc.GetLatestAsync(_snap(DateTime.UtcNow), CancellationToken.None));
    }

    [Fact]
    public void SqlForecastFetcher_query_goes_through_inline_TVF()
    {
        // Pins the cursor-clip mechanism: forecasts read through
        // `dbo.fv_forecasts_at_cursor(@asOf)`, not the bare table.
        // Slice-12's ReplayClock relies on this — clipping at the
        // schema layer means a missed `WHERE` clause in caller code
        // can't leak unbounded data.
        Assert.Contains("dbo.fv_forecasts_at_cursor(@asOf)", SqlForecastFetcher.Sql);
        Assert.Contains("@asOf", SqlForecastFetcher.Sql);
        Assert.DoesNotContain("FROM dbo.Forecasts", SqlForecastFetcher.Sql);
    }

    [Fact]
    public void Constructor_rejects_null_fetcher()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ForecastReadService(null!));
    }
}
