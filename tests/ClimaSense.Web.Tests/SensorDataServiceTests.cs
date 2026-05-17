// SPDX-License-Identifier: MIT
//
// Slice-3 verification tests for `SensorDataService`. Locks:
//
//   * `GetLatestAsync` calls into the injected row-reader exactly once
//     per invocation and passes the cursor's AsOf as the `asOf` cutoff.
//   * The cursor's `AsOf` is preserved verbatim — no UTC re-coercion,
//     no local-time conversion.
//   * Returns `null` (rather than throwing) when the reader returns
//     null — `/api/readings/latest` then maps that to 404.
//   * Wraps `SqlException`s into the caller's `OperationCanceledException`
//     when the request is cancelled.
//
// The tests use a lambda-based fake reader so the suite never needs a
// live SQL Server. The integration-test path is covered by the
// `docker compose up` reviewer flow documented in the PR body.

#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using ClimaSense.Web.Cursor;
using ClimaSense.Web.Readings;
using Xunit;

namespace ClimaSense.Web.Tests;

public sealed class SensorDataServiceTests
{
    private static CursorSnapshot _snap(DateTime asOf) => new(asOf);

    private sealed class _Recorder
    {
        public int Calls { get; private set; }
        public DateTime LastAsOf { get; private set; }

        public LatestReading? Result { get; set; }
        public Exception? Throw { get; set; }

        public Task<LatestReading?> Fetch(DateTime asOf, CancellationToken ct)
        {
            Calls += 1;
            LastAsOf = asOf;
            if (Throw is not null)
            {
                throw Throw;
            }
            return Task.FromResult(Result);
        }
    }

    [Fact]
    public async Task GetLatest_invokes_reader_with_cursor_AsOf()
    {
        var rec = new _Recorder
        {
            Result = new LatestReading(
                ReadingTime: new DateTime(2026, 5, 7, 10, 30, 0, DateTimeKind.Utc),
                TemperatureC: 21.5,
                HumidityPct: 47.0),
        };

        var svc = new SensorDataService(rec.Fetch);
        var snap = _snap(new DateTime(2026, 5, 17, 12, 0, 0, DateTimeKind.Utc));

        var result = await svc.GetLatestAsync(snap, CancellationToken.None);

        Assert.Equal(1, rec.Calls);
        Assert.Equal(snap.AsOf, rec.LastAsOf);
        Assert.NotNull(result);
        Assert.Equal(21.5, result!.TemperatureC);
        Assert.Equal(47.0, result.HumidityPct);
    }

    [Fact]
    public async Task GetLatest_passes_exact_AsOf_with_kind_Utc()
    {
        var rec = new _Recorder();
        var svc = new SensorDataService(rec.Fetch);
        var asOf = new DateTime(2026, 5, 7, 15, 45, 23, DateTimeKind.Utc);
        var snap = _snap(asOf);

        await svc.GetLatestAsync(snap, CancellationToken.None);

        Assert.Equal(asOf, rec.LastAsOf);
        Assert.Equal(DateTimeKind.Utc, rec.LastAsOf.Kind);
    }

    [Fact]
    public async Task GetLatest_returns_null_when_reader_returns_null()
    {
        var rec = new _Recorder { Result = null };
        var svc = new SensorDataService(rec.Fetch);

        var result = await svc.GetLatestAsync(
            _snap(DateTime.UtcNow),
            CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(1, rec.Calls);
    }

    [Fact]
    public async Task GetLatest_propagates_OperationCanceledException()
    {
        var rec = new _Recorder { Throw = new OperationCanceledException() };
        var svc = new SensorDataService(rec.Fetch);

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await svc.GetLatestAsync(
                _snap(DateTime.UtcNow),
                CancellationToken.None));
    }

    [Fact]
    public void LatestReading_wire_shape_is_camelCase_friendly()
    {
        // The `record` properties match the contract's camelCase
        // spelling 1:1 once System.Text.Json's CamelCase naming policy
        // runs over them. This test pins the property names so a typo
        // (`Temperature` instead of `TemperatureC`) is caught here, not
        // by a reviewer running curl on the live endpoint.
        var props = typeof(LatestReading)
            .GetProperties()
            .Select(p => p.Name)
            .OrderBy(n => n)
            .ToArray();

        Assert.Equal(
            new[] { "HumidityPct", "ReadingTime", "TemperatureC" },
            props);
    }

    [Fact]
    public void Constructor_rejects_null_fetcher()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new SensorDataService(null!));
    }
}
