# ClimaSense UPS-Room Monitor — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a read-only .NET 10 ASP.NET Core dashboard that shows live and historical temperature/humidity from `ups3.dbo.tbl_sensor_data` and flags equipment-envelope excursions and stale feeds, deployable to IIS.

**Architecture:** One web project with isolated pure-domain logic (bands, freshness, bucketing, excursions), a Dapper data layer that aggregates server-side (with a memory-cache decorator), a thin Minimal-API surface, and two Razor Pages (Live, History) rendering Chart.js. Spec: `docs/superpowers/specs/2026-06-15-climasense-ups3-monitor-design.md`.

**Tech Stack:** .NET 10, ASP.NET Core (Razor Pages + Minimal API), Dapper, Microsoft.Data.SqlClient, Chart.js, xUnit, Microsoft.AspNetCore.Mvc.Testing.

---

## File Structure

```
src/ClimaSense.Monitor/
  Program.cs                         # DI, options, endpoints, pages, health
  appsettings.json                   # Envelope defaults, logging
  appsettings.Production.json        # prod log levels
  Domain/
    SensorReading.cs                 # reading record
    EnvelopeOptions.cs               # Range + EnvelopeOptions (config)
    BandEvaluator.cs                 # ReadingBand + Classify/Worst (pure)
    Time.cs                          # IClock, SystemClock, CetZone, Freshness (pure)
    Aggregation.cs                   # SeriesPoint, DailyAggregate, BucketSelector (pure)
    Excursion.cs                     # Metric, Excursion, ExcursionDetector (pure)
  Data/
    ISensorReadingRepository.cs      # data port
    SqlSensorReadingRepository.cs    # Dapper impl (server-side aggregation)
    CachingSensorReadingRepository.cs# IMemoryCache decorator
  Services/
    ReadingsService.cs               # composes repo + domain (LatestStatus, series, excursions)
    LatestStatus.cs                  # view record
  Endpoints/
    ReadingsApi.cs                   # /api/readings/* + range resolution
  Pages/
    Index.cshtml(.cs)                # Live
    History.cshtml(.cs)              # History
    Shared/_Layout.cshtml
  wwwroot/
    css/site.css                     # dark theme
    js/charts.js  js/live.js  js/history.js
scripts/
  ups3-index.sql                     # recommended nonclustered index
  climasense_ro.sql                  # optional least-privilege login
tests/ClimaSense.Monitor.Tests/
  Domain/  Services/  Endpoints/  Integration/
```

---

### Task 1: Scaffold projects

**Files:**
- Create: `src/ClimaSense.Monitor/` (webapp template), `tests/ClimaSense.Monitor.Tests/` (xunit template)

- [ ] **Step 1: Create projects and references**

Run:
```bash
cd /Users/arthur/Developer/github/arthurkahwa/climasense
dotnet new webapp -n ClimaSense.Monitor -o src/ClimaSense.Monitor -f net10.0
dotnet new xunit  -n ClimaSense.Monitor.Tests -o tests/ClimaSense.Monitor.Tests -f net10.0
dotnet sln ClimaSense.sln add src/ClimaSense.Monitor tests/ClimaSense.Monitor.Tests
dotnet add tests/ClimaSense.Monitor.Tests reference src/ClimaSense.Monitor
dotnet add src/ClimaSense.Monitor package Dapper
dotnet add src/ClimaSense.Monitor package Microsoft.Data.SqlClient
dotnet add tests/ClimaSense.Monitor.Tests package Microsoft.AspNetCore.Mvc.Testing
```

- [ ] **Step 2: Ensure ICU globalization (SqlClient requirement)**

Confirm `src/ClimaSense.Monitor/ClimaSense.Monitor.csproj` does **not** contain `<InvariantGlobalization>true</InvariantGlobalization>`. If present, remove that line.

- [ ] **Step 3: Build to verify**

Run: `dotnet build ClimaSense.sln`
Expected: Build succeeded (existing projects may also build; new ones succeed).

- [ ] **Step 4: Run the template test**

Run: `dotnet test tests/ClimaSense.Monitor.Tests`
Expected: PASS (template includes one passing test, or zero tests — either is fine).

- [ ] **Step 5: Commit**

```bash
git checkout -b redesign/ups3-monitor
git add src/ClimaSense.Monitor tests/ClimaSense.Monitor.Tests ClimaSense.sln
git commit -m "chore: scaffold ClimaSense.Monitor web + test projects"
```

---

### Task 2: Domain — SensorReading + EnvelopeOptions

**Files:**
- Create: `src/ClimaSense.Monitor/Domain/SensorReading.cs`
- Create: `src/ClimaSense.Monitor/Domain/EnvelopeOptions.cs`
- Test: `tests/ClimaSense.Monitor.Tests/Domain/EnvelopeOptionsTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/ClimaSense.Monitor.Tests/Domain/EnvelopeOptionsTests.cs
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
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/ClimaSense.Monitor.Tests --filter EnvelopeOptionsTests`
Expected: FAIL — `EnvelopeOptions` does not exist (compile error).

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/ClimaSense.Monitor/Domain/SensorReading.cs
namespace ClimaSense.Monitor.Domain;

/// <summary>One reading. Timestamp is CET wall-clock (DateTimeKind.Unspecified).</summary>
public readonly record struct SensorReading(long Id, DateTime Timestamp, int TemperatureC, int HumidityPct);
```

```csharp
// src/ClimaSense.Monitor/Domain/EnvelopeOptions.cs
namespace ClimaSense.Monitor.Domain;

public sealed class Range
{
    public double Min { get; set; }
    public double Max { get; set; }
}

public sealed class EnvelopeOptions
{
    public const string SectionName = "Envelope";
    public Range TemperatureRecommended { get; set; } = new() { Min = 18, Max = 27 };
    public Range TemperatureAllowable   { get; set; } = new() { Min = 15, Max = 32 };
    public Range HumidityRecommended    { get; set; } = new() { Min = 20, Max = 80 };
    public Range HumidityAllowable      { get; set; } = new() { Min = 8,  Max = 90 };
    public int FreshnessMinutes { get; set; } = 30;
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/ClimaSense.Monitor.Tests --filter EnvelopeOptionsTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/ClimaSense.Monitor/Domain tests/ClimaSense.Monitor.Tests/Domain
git commit -m "feat(domain): SensorReading + EnvelopeOptions with ASHRAE defaults"
```

---

### Task 3: Domain — BandEvaluator

**Files:**
- Create: `src/ClimaSense.Monitor/Domain/BandEvaluator.cs`
- Test: `tests/ClimaSense.Monitor.Tests/Domain/BandEvaluatorTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/ClimaSense.Monitor.Tests/Domain/BandEvaluatorTests.cs
using ClimaSense.Monitor.Domain;
using Xunit;

namespace ClimaSense.Monitor.Tests.Domain;

public class BandEvaluatorTests
{
    static readonly Range Rec = new() { Min = 18, Max = 27 };
    static readonly Range All = new() { Min = 15, Max = 32 };

    [Theory]
    [InlineData(20, ReadingBand.Recommended)]
    [InlineData(18, ReadingBand.Recommended)]
    [InlineData(27, ReadingBand.Recommended)]
    [InlineData(16, ReadingBand.Allowable)]
    [InlineData(32, ReadingBand.Allowable)]
    [InlineData(35, ReadingBand.OutOfRange)]
    [InlineData(10, ReadingBand.OutOfRange)]
    public void Classify_buckets_value(double value, ReadingBand expected)
        => Assert.Equal(expected, BandEvaluator.Classify(value, Rec, All));

    [Fact]
    public void Worst_returns_more_severe_band()
        => Assert.Equal(ReadingBand.OutOfRange,
            BandEvaluator.Worst(ReadingBand.Recommended, ReadingBand.OutOfRange));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/ClimaSense.Monitor.Tests --filter BandEvaluatorTests`
Expected: FAIL — `BandEvaluator` / `ReadingBand` not defined.

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/ClimaSense.Monitor/Domain/BandEvaluator.cs
namespace ClimaSense.Monitor.Domain;

public enum ReadingBand { Recommended = 0, Allowable = 1, OutOfRange = 2 }

public static class BandEvaluator
{
    public static ReadingBand Classify(double value, Range recommended, Range allowable)
    {
        if (value >= recommended.Min && value <= recommended.Max) return ReadingBand.Recommended;
        if (value >= allowable.Min && value <= allowable.Max) return ReadingBand.Allowable;
        return ReadingBand.OutOfRange;
    }

    public static ReadingBand Worst(ReadingBand a, ReadingBand b)
        => (ReadingBand)System.Math.Max((int)a, (int)b);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/ClimaSense.Monitor.Tests --filter BandEvaluatorTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/ClimaSense.Monitor/Domain/BandEvaluator.cs tests/ClimaSense.Monitor.Tests/Domain/BandEvaluatorTests.cs
git commit -m "feat(domain): BandEvaluator band classification"
```

---

### Task 4: Domain — Time (CetZone + Freshness)

**Files:**
- Create: `src/ClimaSense.Monitor/Domain/Time.cs`
- Test: `tests/ClimaSense.Monitor.Tests/Domain/TimeTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/ClimaSense.Monitor.Tests/Domain/TimeTests.cs
using ClimaSense.Monitor.Domain;
using Xunit;

namespace ClimaSense.Monitor.Tests.Domain;

public class TimeTests
{
    [Fact]
    public void Cet_summer_reading_converts_to_utc_minus_two()
    {
        // 2026-06-15 18:00 CET (CEST, UTC+2) -> 16:00 UTC
        var utc = CetZone.ToUtc(new DateTime(2026, 6, 15, 18, 0, 0));
        Assert.Equal(new DateTime(2026, 6, 15, 16, 0, 0, DateTimeKind.Utc), utc);
    }

    [Fact]
    public void IsStale_true_when_older_than_threshold()
    {
        var readingCet = new DateTime(2026, 6, 15, 18, 0, 0);       // 16:00 UTC
        var nowUtc = new DateTime(2026, 6, 15, 16, 45, 0, DateTimeKind.Utc); // 45 min later
        Assert.True(Freshness.IsStale(readingCet, nowUtc, 30));
        Assert.Equal(45, Freshness.MinutesOld(readingCet, nowUtc));
    }

    [Fact]
    public void IsStale_false_when_fresh()
    {
        var readingCet = new DateTime(2026, 6, 15, 18, 0, 0);
        var nowUtc = new DateTime(2026, 6, 15, 16, 10, 0, DateTimeKind.Utc);
        Assert.False(Freshness.IsStale(readingCet, nowUtc, 30));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/ClimaSense.Monitor.Tests --filter TimeTests`
Expected: FAIL — `CetZone` / `Freshness` not defined.

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/ClimaSense.Monitor/Domain/Time.cs
namespace ClimaSense.Monitor.Domain;

public interface IClock { DateTime UtcNow { get; } }

public sealed class SystemClock : IClock { public DateTime UtcNow => DateTime.UtcNow; }

public static class CetZone
{
    // .NET 10 resolves IANA ids on both Windows and Unix.
    public static readonly TimeZoneInfo Cet = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    public static DateTime ToUtc(DateTime cetWallClock)
        => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(cetWallClock, DateTimeKind.Unspecified), Cet);

    public static DateTime FromUtc(DateTime utc)
        => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Cet);
}

public static class Freshness
{
    public static bool IsStale(DateTime readingCet, DateTime nowUtc, int freshnessMinutes)
        => nowUtc - CetZone.ToUtc(readingCet) > TimeSpan.FromMinutes(freshnessMinutes);

    public static int MinutesOld(DateTime readingCet, DateTime nowUtc)
        => (int)Math.Max(0, (nowUtc - CetZone.ToUtc(readingCet)).TotalMinutes);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/ClimaSense.Monitor.Tests --filter TimeTests`
Expected: PASS. (If the runner lacks the `Europe/Berlin` tz database, install ICU/tzdata; macOS and Windows ship it.)

- [ ] **Step 5: Commit**

```bash
git add src/ClimaSense.Monitor/Domain/Time.cs tests/ClimaSense.Monitor.Tests/Domain/TimeTests.cs
git commit -m "feat(domain): CET zone + freshness logic"
```

---

### Task 5: Domain — Aggregation (BucketSelector + DTOs)

**Files:**
- Create: `src/ClimaSense.Monitor/Domain/Aggregation.cs`
- Test: `tests/ClimaSense.Monitor.Tests/Domain/BucketSelectorTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/ClimaSense.Monitor.Tests/Domain/BucketSelectorTests.cs
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
    public void BucketMinutes_scales_with_range(double days, int expected)
        => Assert.Equal(expected, BucketSelector.BucketMinutes(TimeSpan.FromDays(days)));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/ClimaSense.Monitor.Tests --filter BucketSelectorTests`
Expected: FAIL — `BucketSelector` not defined.

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/ClimaSense.Monitor/Domain/Aggregation.cs
namespace ClimaSense.Monitor.Domain;

public readonly record struct SeriesPoint(
    DateTime BucketStartCet,
    double AvgTemp, int MinTemp, int MaxTemp,
    double AvgHumidity, int MinHumidity, int MaxHumidity,
    int Count);

public readonly record struct DailyAggregate(
    DateOnly DateCet,
    double AvgTemp, int MinTemp, int MaxTemp,
    double AvgHumidity, int MinHumidity, int MaxHumidity,
    int Count);

public static class BucketSelector
{
    public static int BucketMinutes(TimeSpan range) => range.TotalDays switch
    {
        <= 2  => 15,
        <= 14 => 60,
        <= 90 => 360,
        _     => 1440,
    };
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/ClimaSense.Monitor.Tests --filter BucketSelectorTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/ClimaSense.Monitor/Domain/Aggregation.cs tests/ClimaSense.Monitor.Tests/Domain/BucketSelectorTests.cs
git commit -m "feat(domain): aggregation DTOs + bucket selection"
```

---

### Task 6: Domain — ExcursionDetector

**Files:**
- Create: `src/ClimaSense.Monitor/Domain/Excursion.cs`
- Test: `tests/ClimaSense.Monitor.Tests/Domain/ExcursionDetectorTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/ClimaSense.Monitor.Tests/Domain/ExcursionDetectorTests.cs
using System;
using System.Collections.Generic;
using ClimaSense.Monitor.Domain;
using Xunit;

namespace ClimaSense.Monitor.Tests.Domain;

public class ExcursionDetectorTests
{
    static readonly Range Rec = new() { Min = 18, Max = 27 };
    static readonly Range All = new() { Min = 15, Max = 32 };

    static SeriesPoint P(int hour, double temp) =>
        new(new DateTime(2026, 6, 15, hour, 0, 0), temp, (int)temp, (int)temp, 50, 50, 50, 4);

    [Fact]
    public void Detect_finds_one_contiguous_run_with_peak_and_band()
    {
        var series = new List<SeriesPoint> { P(0, 20), P(1, 30), P(2, 35), P(3, 20) }; // run at 01:00-02:00
        var ex = ExcursionDetector.Detect(series, Metric.Temperature, 60, Rec, All);

        var e = Assert.Single(ex);
        Assert.Equal(new DateTime(2026, 6, 15, 1, 0, 0), e.StartCet);
        Assert.Equal(new DateTime(2026, 6, 15, 3, 0, 0), e.EndCet); // last bucket start (02:00) + 60 min
        Assert.Equal(120, e.DurationMinutes);
        Assert.Equal(35, e.Peak);                 // furthest above recommended max (27)
        Assert.Equal(ReadingBand.OutOfRange, e.Band); // 35 is out of allowable
    }

    [Fact]
    public void Detect_returns_empty_when_all_recommended()
        => Assert.Empty(ExcursionDetector.Detect(
            new List<SeriesPoint> { P(0, 20), P(1, 21) }, Metric.Temperature, 60, Rec, All));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/ClimaSense.Monitor.Tests --filter ExcursionDetectorTests`
Expected: FAIL — `ExcursionDetector` / `Metric` / `Excursion` not defined.

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/ClimaSense.Monitor/Domain/Excursion.cs
namespace ClimaSense.Monitor.Domain;

public enum Metric { Temperature, Humidity }

public readonly record struct Excursion(
    Metric Metric, DateTime StartCet, DateTime EndCet, int DurationMinutes, double Peak, ReadingBand Band);

public static class ExcursionDetector
{
    /// <summary>Maximal contiguous runs where the metric leaves the Recommended band.</summary>
    public static IReadOnlyList<Excursion> Detect(
        IReadOnlyList<SeriesPoint> series, Metric metric, int bucketMinutes, Range recommended, Range allowable)
    {
        var result = new List<Excursion>();
        int runStart = -1;
        for (int i = 0; i < series.Count; i++)
        {
            var band = BandEvaluator.Classify(Value(series[i], metric), recommended, allowable);
            bool inRun = band != ReadingBand.Recommended;
            if (inRun && runStart < 0) runStart = i;
            else if (!inRun && runStart >= 0) { result.Add(Build(series, metric, bucketMinutes, recommended, allowable, runStart, i - 1)); runStart = -1; }
        }
        if (runStart >= 0) result.Add(Build(series, metric, bucketMinutes, recommended, allowable, runStart, series.Count - 1));
        return result;
    }

    static double Value(SeriesPoint p, Metric m) => m == Metric.Temperature ? p.AvgTemp : p.AvgHumidity;

    static Excursion Build(IReadOnlyList<SeriesPoint> s, Metric metric, int bucketMinutes,
        Range recommended, Range allowable, int from, int to)
    {
        var start = s[from].BucketStartCet;
        var end = s[to].BucketStartCet.AddMinutes(bucketMinutes);
        double peak = recommended.Min, maxDist = -1;
        var worst = ReadingBand.Recommended;
        for (int i = from; i <= to; i++)
        {
            double v = Value(s[i], metric);
            worst = BandEvaluator.Worst(worst, BandEvaluator.Classify(v, recommended, allowable));
            double dist = v < recommended.Min ? recommended.Min - v : v > recommended.Max ? v - recommended.Max : 0;
            if (dist > maxDist) { maxDist = dist; peak = v; }
        }
        return new Excursion(metric, start, end, (int)(end - start).TotalMinutes, peak, worst);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/ClimaSense.Monitor.Tests --filter ExcursionDetectorTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/ClimaSense.Monitor/Domain/Excursion.cs tests/ClimaSense.Monitor.Tests/Domain/ExcursionDetectorTests.cs
git commit -m "feat(domain): excursion detection over aggregated series"
```

---

### Task 7: Service — ReadingsService + repository port

**Files:**
- Create: `src/ClimaSense.Monitor/Data/ISensorReadingRepository.cs`
- Create: `src/ClimaSense.Monitor/Services/LatestStatus.cs`
- Create: `src/ClimaSense.Monitor/Services/ReadingsService.cs`
- Test: `tests/ClimaSense.Monitor.Tests/Services/ReadingsServiceTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/ClimaSense.Monitor.Tests/Services/ReadingsServiceTests.cs
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClimaSense.Monitor.Data;
using ClimaSense.Monitor.Domain;
using ClimaSense.Monitor.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace ClimaSense.Monitor.Tests.Services;

public class ReadingsServiceTests
{
    sealed class FakeRepo : ISensorReadingRepository
    {
        public SensorReading? Latest;
        public int GetLatestCalls;
        public Task<SensorReading?> GetLatestAsync(CancellationToken ct = default) { GetLatestCalls++; return Task.FromResult(Latest); }
        public Task<IReadOnlyList<SeriesPoint>> GetSeriesAsync(DateTime f, DateTime t, int b, CancellationToken ct = default)
            => Task.FromResult((IReadOnlyList<SeriesPoint>)Array.Empty<SeriesPoint>());
        public Task<IReadOnlyList<DailyAggregate>> GetDailyAggregatesAsync(DateTime f, DateTime t, CancellationToken ct = default)
            => Task.FromResult((IReadOnlyList<DailyAggregate>)Array.Empty<DailyAggregate>());
    }
    sealed class FixedClock(DateTime utc) : IClock { public DateTime UtcNow => utc; }

    static ReadingsService Build(FakeRepo repo, DateTime nowUtc)
        => new(repo, Options.Create(new EnvelopeOptions()), new FixedClock(nowUtc));

    [Fact]
    public async Task LatestStatus_classifies_bands_and_flags_stale()
    {
        var repo = new FakeRepo { Latest = new SensorReading(1, new DateTime(2026, 6, 15, 18, 0, 0), 35, 50) };
        var s = await Build(repo, new DateTime(2026, 6, 15, 16, 45, 0, DateTimeKind.Utc)).GetLatestStatusAsync();

        Assert.NotNull(s);
        Assert.Equal(ReadingBand.OutOfRange, s.Value.TempBand);
        Assert.Equal(ReadingBand.Recommended, s.Value.HumidityBand);
        Assert.Equal(ReadingBand.OutOfRange, s.Value.Overall);
        Assert.True(s.Value.IsStale);
    }

    [Fact]
    public async Task LatestStatus_null_when_no_data()
        => Assert.Null(await Build(new FakeRepo(), DateTime.UtcNow).GetLatestStatusAsync());
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/ClimaSense.Monitor.Tests --filter ReadingsServiceTests`
Expected: FAIL — `ISensorReadingRepository`, `ReadingsService`, `LatestStatus` not defined.

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/ClimaSense.Monitor/Data/ISensorReadingRepository.cs
using ClimaSense.Monitor.Domain;

namespace ClimaSense.Monitor.Data;

public interface ISensorReadingRepository
{
    Task<SensorReading?> GetLatestAsync(CancellationToken ct = default);
    Task<IReadOnlyList<SeriesPoint>> GetSeriesAsync(DateTime fromCet, DateTime toCet, int bucketMinutes, CancellationToken ct = default);
    Task<IReadOnlyList<DailyAggregate>> GetDailyAggregatesAsync(DateTime fromCet, DateTime toCet, CancellationToken ct = default);
}
```

```csharp
// src/ClimaSense.Monitor/Services/LatestStatus.cs
using ClimaSense.Monitor.Domain;

namespace ClimaSense.Monitor.Services;

public readonly record struct LatestStatus(
    SensorReading Reading, ReadingBand TempBand, ReadingBand HumidityBand, ReadingBand Overall,
    int MinutesOld, bool IsStale);
```

```csharp
// src/ClimaSense.Monitor/Services/ReadingsService.cs
using ClimaSense.Monitor.Data;
using ClimaSense.Monitor.Domain;
using Microsoft.Extensions.Options;

namespace ClimaSense.Monitor.Services;

public sealed class ReadingsService(ISensorReadingRepository repo, IOptions<EnvelopeOptions> options, IClock clock)
{
    readonly EnvelopeOptions _env = options.Value;

    public async Task<LatestStatus?> GetLatestStatusAsync(CancellationToken ct = default)
    {
        var r = await repo.GetLatestAsync(ct);
        if (r is null) return null;
        var reading = r.Value;
        var t = BandEvaluator.Classify(reading.TemperatureC, _env.TemperatureRecommended, _env.TemperatureAllowable);
        var h = BandEvaluator.Classify(reading.HumidityPct, _env.HumidityRecommended, _env.HumidityAllowable);
        var nowUtc = clock.UtcNow;
        return new LatestStatus(reading, t, h, BandEvaluator.Worst(t, h),
            Freshness.MinutesOld(reading.Timestamp, nowUtc),
            Freshness.IsStale(reading.Timestamp, nowUtc, _env.FreshnessMinutes));
    }

    public Task<IReadOnlyList<SeriesPoint>> GetSeriesAsync(DateTime fromCet, DateTime toCet, CancellationToken ct = default)
        => repo.GetSeriesAsync(fromCet, toCet, BucketSelector.BucketMinutes(toCet - fromCet), ct);

    public Task<IReadOnlyList<DailyAggregate>> GetDailyAsync(DateTime fromCet, DateTime toCet, CancellationToken ct = default)
        => repo.GetDailyAggregatesAsync(fromCet, toCet, ct);

    public async Task<IReadOnlyList<Excursion>> GetExcursionsAsync(DateTime fromCet, DateTime toCet, CancellationToken ct = default)
    {
        int bucket = BucketSelector.BucketMinutes(toCet - fromCet);
        var series = await repo.GetSeriesAsync(fromCet, toCet, bucket, ct);
        var temp = ExcursionDetector.Detect(series, Metric.Temperature, bucket, _env.TemperatureRecommended, _env.TemperatureAllowable);
        var hum  = ExcursionDetector.Detect(series, Metric.Humidity, bucket, _env.HumidityRecommended, _env.HumidityAllowable);
        return temp.Concat(hum).OrderByDescending(e => e.StartCet).ToList();
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/ClimaSense.Monitor.Tests --filter ReadingsServiceTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/ClimaSense.Monitor/Data/ISensorReadingRepository.cs src/ClimaSense.Monitor/Services tests/ClimaSense.Monitor.Tests/Services
git commit -m "feat(service): ReadingsService composing repo + domain"
```

---

### Task 8: Data — SqlSensorReadingRepository (Dapper)

**Files:**
- Create: `src/ClimaSense.Monitor/Data/SqlSensorReadingRepository.cs`
- Test: `tests/ClimaSense.Monitor.Tests/Integration/SqlSensorReadingRepositoryTests.cs`

- [ ] **Step 1: Add the skippable-integration package**

Run: `dotnet add tests/ClimaSense.Monitor.Tests package Xunit.SkippableFact`

- [ ] **Step 2: Write the failing (integration) test**

```csharp
// tests/ClimaSense.Monitor.Tests/Integration/SqlSensorReadingRepositoryTests.cs
using System;
using System.Threading.Tasks;
using ClimaSense.Monitor.Data;
using Xunit;

namespace ClimaSense.Monitor.Tests.Integration;

[Trait("Category", "Integration")]
public class SqlSensorReadingRepositoryTests
{
    static string? Conn => Environment.GetEnvironmentVariable("CLIMASENSE_UPS3_CONNECTION");

    [SkippableFact]
    public async Task GetLatest_returns_a_reading()
    {
        Skip.If(string.IsNullOrEmpty(Conn), "CLIMASENSE_UPS3_CONNECTION not set");
        var repo = new SqlSensorReadingRepository(Conn!);
        var r = await repo.GetLatestAsync();
        Assert.NotNull(r);
        Assert.True(r!.Value.Id > 0);
    }

    [SkippableFact]
    public async Task GetSeries_last24h_returns_buckets()
    {
        Skip.If(string.IsNullOrEmpty(Conn), "CLIMASENSE_UPS3_CONNECTION not set");
        var repo = new SqlSensorReadingRepository(Conn!);
        var now = DateTime.Now;                       // CET wall-clock on the dev box
        var series = await repo.GetSeriesAsync(now.AddHours(-24), now, 15);
        Assert.NotEmpty(series);
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/ClimaSense.Monitor.Tests --filter SqlSensorReadingRepositoryTests`
Expected: FAIL — `SqlSensorReadingRepository` not defined.

- [ ] **Step 4: Write minimal implementation**

```csharp
// src/ClimaSense.Monitor/Data/SqlSensorReadingRepository.cs
using System.Data;
using ClimaSense.Monitor.Domain;
using Dapper;
using Microsoft.Data.SqlClient;

namespace ClimaSense.Monitor.Data;

public sealed class SqlSensorReadingRepository : ISensorReadingRepository
{
    static SqlSensorReadingRepository() => SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());

    readonly string _connectionString;
    public SqlSensorReadingRepository(string connectionString) => _connectionString = connectionString;

    public async Task<SensorReading?> GetLatestAsync(CancellationToken ct = default)
    {
        const string sql = "SELECT TOP 1 id, sensor_dateTime, temperature, humidity FROM dbo.tbl_sensor_data ORDER BY sensor_dateTime DESC;";
        await using var con = new SqlConnection(_connectionString);
        var row = await con.QueryFirstOrDefaultAsync<Row>(new CommandDefinition(sql, commandTimeout: 15, cancellationToken: ct));
        return row is null ? null : new SensorReading(row.id, row.sensor_dateTime, row.temperature, row.humidity);
    }

    public async Task<IReadOnlyList<SeriesPoint>> GetSeriesAsync(DateTime fromCet, DateTime toCet, int bucketMinutes, CancellationToken ct = default)
    {
        const string sql = """
            SELECT DATEADD(MINUTE,(DATEDIFF(MINUTE,'2000-01-01',sensor_dateTime)/@bucket)*@bucket,'2000-01-01') AS BucketStartCet,
                   AVG(CAST(temperature AS float)) AS AvgTemp, MIN(temperature) AS MinTemp, MAX(temperature) AS MaxTemp,
                   AVG(CAST(humidity   AS float)) AS AvgHumidity, MIN(humidity) AS MinHumidity, MAX(humidity) AS MaxHumidity,
                   COUNT(*) AS Count
            FROM dbo.tbl_sensor_data
            WHERE sensor_dateTime >= @from AND sensor_dateTime < @to
            GROUP BY DATEADD(MINUTE,(DATEDIFF(MINUTE,'2000-01-01',sensor_dateTime)/@bucket)*@bucket,'2000-01-01')
            ORDER BY BucketStartCet;
            """;
        await using var con = new SqlConnection(_connectionString);
        var rows = await con.QueryAsync<SeriesPoint>(new CommandDefinition(sql,
            new { from = fromCet, to = toCet, bucket = bucketMinutes }, commandTimeout: 30, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<DailyAggregate>> GetDailyAggregatesAsync(DateTime fromCet, DateTime toCet, CancellationToken ct = default)
    {
        const string sql = """
            SELECT CAST(sensor_dateTime AS date) AS DateCet,
                   AVG(CAST(temperature AS float)) AS AvgTemp, MIN(temperature) AS MinTemp, MAX(temperature) AS MaxTemp,
                   AVG(CAST(humidity   AS float)) AS AvgHumidity, MIN(humidity) AS MinHumidity, MAX(humidity) AS MaxHumidity,
                   COUNT(*) AS Count
            FROM dbo.tbl_sensor_data
            WHERE sensor_dateTime >= @from AND sensor_dateTime < @to
            GROUP BY CAST(sensor_dateTime AS date)
            ORDER BY DateCet;
            """;
        await using var con = new SqlConnection(_connectionString);
        var rows = await con.QueryAsync<DailyAggregate>(new CommandDefinition(sql,
            new { from = fromCet, to = toCet }, commandTimeout: 30, cancellationToken: ct));
        return rows.AsList();
    }

    sealed record Row(long id, DateTime sensor_dateTime, int temperature, int humidity);

    sealed class DateOnlyTypeHandler : SqlMapper.TypeHandler<DateOnly>
    {
        public override DateOnly Parse(object value) => DateOnly.FromDateTime((DateTime)value);
        public override void SetValue(IDbDataParameter parameter, DateOnly value)
        { parameter.DbType = DbType.Date; parameter.Value = value.ToDateTime(TimeOnly.MinValue); }
    }
}
```

- [ ] **Step 5: Run test to verify it passes (with the DB reachable)**

Run:
```bash
export CLIMASENSE_UPS3_CONNECTION='Server=util02.lab.local,1433;Database=ups3;User ID=<your-db-user>;Password=<your-db-password>;Encrypt=True;TrustServerCertificate=True'
dotnet test tests/ClimaSense.Monitor.Tests --filter SqlSensorReadingRepositoryTests
```
Expected: PASS (2 tests). If `CLIMASENSE_UPS3_CONNECTION` is unset, tests SKIP (still green).

- [ ] **Step 6: Commit**

```bash
git add src/ClimaSense.Monitor/Data/SqlSensorReadingRepository.cs tests/ClimaSense.Monitor.Tests/Integration tests/ClimaSense.Monitor.Tests/ClimaSense.Monitor.Tests.csproj
git commit -m "feat(data): Dapper SqlSensorReadingRepository with server-side aggregation"
```

---

### Task 9: Data — caching decorator

**Files:**
- Create: `src/ClimaSense.Monitor/Data/CachingSensorReadingRepository.cs`
- Test: `tests/ClimaSense.Monitor.Tests/Data/CachingSensorReadingRepositoryTests.cs`

- [ ] **Step 1: Add memory-cache package to the test project**

Run: `dotnet add tests/ClimaSense.Monitor.Tests package Microsoft.Extensions.Caching.Memory`

- [ ] **Step 2: Write the failing test**

```csharp
// tests/ClimaSense.Monitor.Tests/Data/CachingSensorReadingRepositoryTests.cs
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClimaSense.Monitor.Data;
using ClimaSense.Monitor.Domain;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace ClimaSense.Monitor.Tests.Data;

public class CachingSensorReadingRepositoryTests
{
    sealed class CountingRepo : ISensorReadingRepository
    {
        public int LatestCalls;
        public Task<SensorReading?> GetLatestAsync(CancellationToken ct = default)
        { LatestCalls++; return Task.FromResult<SensorReading?>(new SensorReading(1, DateTime.Now, 20, 50)); }
        public Task<IReadOnlyList<SeriesPoint>> GetSeriesAsync(DateTime f, DateTime t, int b, CancellationToken ct = default)
            => Task.FromResult((IReadOnlyList<SeriesPoint>)Array.Empty<SeriesPoint>());
        public Task<IReadOnlyList<DailyAggregate>> GetDailyAggregatesAsync(DateTime f, DateTime t, CancellationToken ct = default)
            => Task.FromResult((IReadOnlyList<DailyAggregate>)Array.Empty<DailyAggregate>());
    }

    [Fact]
    public async Task GetLatest_is_cached()
    {
        var inner = new CountingRepo();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var sut = new CachingSensorReadingRepository(inner, cache);
        await sut.GetLatestAsync();
        await sut.GetLatestAsync();
        Assert.Equal(1, inner.LatestCalls);
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/ClimaSense.Monitor.Tests --filter CachingSensorReadingRepositoryTests`
Expected: FAIL — `CachingSensorReadingRepository` not defined.

- [ ] **Step 4: Write minimal implementation**

```csharp
// src/ClimaSense.Monitor/Data/CachingSensorReadingRepository.cs
using ClimaSense.Monitor.Domain;
using Microsoft.Extensions.Caching.Memory;

namespace ClimaSense.Monitor.Data;

public sealed class CachingSensorReadingRepository(ISensorReadingRepository inner, IMemoryCache cache) : ISensorReadingRepository
{
    public Task<SensorReading?> GetLatestAsync(CancellationToken ct = default)
        => cache.GetOrCreateAsync("latest", e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);
            return inner.GetLatestAsync(ct);
        })!;

    public Task<IReadOnlyList<SeriesPoint>> GetSeriesAsync(DateTime fromCet, DateTime toCet, int bucketMinutes, CancellationToken ct = default)
        => cache.GetOrCreateAsync($"series:{fromCet:o}:{toCet:o}:{bucketMinutes}", e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            return inner.GetSeriesAsync(fromCet, toCet, bucketMinutes, ct);
        })!;

    public Task<IReadOnlyList<DailyAggregate>> GetDailyAggregatesAsync(DateTime fromCet, DateTime toCet, CancellationToken ct = default)
        => cache.GetOrCreateAsync($"daily:{fromCet:o}:{toCet:o}", e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            return inner.GetDailyAggregatesAsync(fromCet, toCet, ct);
        })!;
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/ClimaSense.Monitor.Tests --filter CachingSensorReadingRepositoryTests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/ClimaSense.Monitor/Data/CachingSensorReadingRepository.cs tests/ClimaSense.Monitor.Tests/Data
git commit -m "feat(data): IMemoryCache decorator over the SQL repository"
```

---

### Task 10: Endpoints — RangeResolver + ReadingsApi

**Files:**
- Create: `src/ClimaSense.Monitor/Endpoints/RangeResolver.cs`
- Create: `src/ClimaSense.Monitor/Endpoints/ReadingsApi.cs`
- Test: `tests/ClimaSense.Monitor.Tests/Endpoints/RangeResolverTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/ClimaSense.Monitor.Tests/Endpoints/RangeResolverTests.cs
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
        => Assert.False(RangeResolver.TryResolve("5y", null, null, NowCet, out _, out _, out _));

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
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/ClimaSense.Monitor.Tests --filter RangeResolverTests`
Expected: FAIL — `RangeResolver` not defined.

- [ ] **Step 3: Write the implementations**

```csharp
// src/ClimaSense.Monitor/Endpoints/RangeResolver.cs
namespace ClimaSense.Monitor.Endpoints;

public static class RangeResolver
{
    static readonly Dictionary<string, TimeSpan> Presets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["24h"] = TimeSpan.FromHours(24),
        ["7d"]  = TimeSpan.FromDays(7),
        ["30d"] = TimeSpan.FromDays(30),
        ["90d"] = TimeSpan.FromDays(90),
        ["1y"]  = TimeSpan.FromDays(365),
    };

    public static bool TryResolve(string? range, DateTime? from, DateTime? to, DateTime nowCet,
        out DateTime fromCet, out DateTime toCet, out string? error)
    {
        error = null; fromCet = default; toCet = default;
        if (!string.IsNullOrEmpty(range))
        {
            if (!Presets.TryGetValue(range, out var span)) { error = $"unknown range '{range}'"; return false; }
            toCet = nowCet; fromCet = nowCet - span; return true;
        }
        if (from is null || to is null) { error = "provide 'range' or both 'from' and 'to'"; return false; }
        if (from >= to) { error = "'from' must be before 'to'"; return false; }
        if (to.Value - from.Value > TimeSpan.FromDays(366 * 2)) { error = "range exceeds 2 years"; return false; }
        fromCet = from.Value; toCet = to.Value; return true;
    }
}
```

```csharp
// src/ClimaSense.Monitor/Endpoints/ReadingsApi.cs
using ClimaSense.Monitor.Domain;
using ClimaSense.Monitor.Services;

namespace ClimaSense.Monitor.Endpoints;

public static class ReadingsApi
{
    public static IEndpointRouteBuilder MapReadingsApi(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/readings");

        g.MapGet("/latest", async (ReadingsService svc, CancellationToken ct) =>
        {
            var s = await svc.GetLatestStatusAsync(ct);
            return s is null ? Results.NotFound() : Results.Ok(s.Value);
        });

        g.MapGet("/series", (string? range, DateTime? from, DateTime? to, ReadingsService svc, IClock clock, CancellationToken ct)
            => Resolve(range, from, to, clock, (f, t) => svc.GetSeriesAsync(f, t, ct)));

        g.MapGet("/daily", (string? range, DateTime? from, DateTime? to, ReadingsService svc, IClock clock, CancellationToken ct)
            => Resolve(range, from, to, clock, (f, t) => svc.GetDailyAsync(f, t, ct)));

        g.MapGet("/excursions", (string? range, DateTime? from, DateTime? to, ReadingsService svc, IClock clock, CancellationToken ct)
            => Resolve(range, from, to, clock, (f, t) => svc.GetExcursionsAsync(f, t, ct)));

        return app;
    }

    static async Task<IResult> Resolve<T>(string? range, DateTime? from, DateTime? to, IClock clock,
        Func<DateTime, DateTime, Task<T>> query)
    {
        if (!RangeResolver.TryResolve(range, from, to, CetZone.FromUtc(clock.UtcNow), out var f, out var t, out var err))
            return Results.BadRequest(new { error = err });
        return Results.Ok(await query(f, t));
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/ClimaSense.Monitor.Tests --filter RangeResolverTests`
Expected: PASS (4 tests). `ReadingsApi` compiles but is not yet mapped (Task 11).

- [ ] **Step 5: Commit**

```bash
git add src/ClimaSense.Monitor/Endpoints tests/ClimaSense.Monitor.Tests/Endpoints
git commit -m "feat(api): readings endpoints + range resolution"
```

---

### Task 11: Host wiring — Program.cs, config, health check

**Files:**
- Create: `src/ClimaSense.Monitor/Services/DbFeedHealthCheck.cs`
- Modify: `src/ClimaSense.Monitor/Program.cs` (replace template contents)
- Modify: `src/ClimaSense.Monitor/appsettings.json`

- [ ] **Step 1: Write the health check**

```csharp
// src/ClimaSense.Monitor/Services/DbFeedHealthCheck.cs
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ClimaSense.Monitor.Services;

public sealed class DbFeedHealthCheck(ReadingsService svc) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            var s = await svc.GetLatestStatusAsync(ct);
            if (s is null) return HealthCheckResult.Degraded("no readings");
            return s.Value.IsStale
                ? HealthCheckResult.Degraded($"feed stale ({s.Value.MinutesOld} min)")
                : HealthCheckResult.Healthy($"fresh ({s.Value.MinutesOld} min)");
        }
        catch (Exception ex) { return HealthCheckResult.Unhealthy("db error", ex); }
    }
}
```

- [ ] **Step 2: Replace `Program.cs` with the wired host**

```csharp
// src/ClimaSense.Monitor/Program.cs
using ClimaSense.Monitor.Data;
using ClimaSense.Monitor.Domain;
using ClimaSense.Monitor.Endpoints;
using ClimaSense.Monitor.Services;
using Microsoft.Extensions.Caching.Memory;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<EnvelopeOptions>(builder.Configuration.GetSection(EnvelopeOptions.SectionName));
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddMemoryCache();

var connectionString = builder.Configuration.GetConnectionString("Ups3")
    ?? Environment.GetEnvironmentVariable("CLIMASENSE_UPS3_CONNECTION")
    ?? throw new InvalidOperationException("Set ConnectionStrings:Ups3 or CLIMASENSE_UPS3_CONNECTION.");

builder.Services.AddSingleton<ISensorReadingRepository>(sp =>
    new CachingSensorReadingRepository(
        new SqlSensorReadingRepository(connectionString),
        sp.GetRequiredService<IMemoryCache>()));
builder.Services.AddScoped<ReadingsService>();

builder.Services.AddRazorPages();
builder.Services.AddHealthChecks().AddCheck<DbFeedHealthCheck>("feed");

var app = builder.Build();

if (!app.Environment.IsDevelopment()) { app.UseExceptionHandler("/Error"); app.UseHsts(); }
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.MapRazorPages();
app.MapReadingsApi();
app.MapHealthChecks("/health");

app.Run();

public partial class Program { }
```

- [ ] **Step 3: Set `appsettings.json`**

```json
{
  "Logging": { "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning" } },
  "AllowedHosts": "*",
  "Envelope": {
    "TemperatureRecommended": { "Min": 18, "Max": 27 },
    "TemperatureAllowable":   { "Min": 15, "Max": 32 },
    "HumidityRecommended":    { "Min": 20, "Max": 80 },
    "HumidityAllowable":      { "Min": 8,  "Max": 90 },
    "FreshnessMinutes": 30
  }
}
```

- [ ] **Step 4: Build to verify wiring compiles**

Run: `dotnet build src/ClimaSense.Monitor`
Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add src/ClimaSense.Monitor/Program.cs src/ClimaSense.Monitor/appsettings.json src/ClimaSense.Monitor/Services/DbFeedHealthCheck.cs
git commit -m "feat(host): DI wiring, config, health check"
```

---

### Task 12: Endpoint + health integration tests (WebApplicationFactory)

**Files:**
- Create: `tests/ClimaSense.Monitor.Tests/Endpoints/ApiTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/ClimaSense.Monitor.Tests/Endpoints/ApiTests.cs
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using ClimaSense.Monitor.Data;
using ClimaSense.Monitor.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace ClimaSense.Monitor.Tests.Endpoints;

public sealed class FakeRepo : ISensorReadingRepository
{
    public SensorReading? Latest = new(1, new DateTime(2026, 6, 15, 18, 0, 0), 19, 49);
    public Task<SensorReading?> GetLatestAsync(CancellationToken ct = default) => Task.FromResult(Latest);
    public Task<IReadOnlyList<SeriesPoint>> GetSeriesAsync(DateTime f, DateTime t, int b, CancellationToken ct = default)
        => Task.FromResult((IReadOnlyList<SeriesPoint>)new[] { new SeriesPoint(f, 19, 18, 20, 49, 48, 50, 4) });
    public Task<IReadOnlyList<DailyAggregate>> GetDailyAggregatesAsync(DateTime f, DateTime t, CancellationToken ct = default)
        => Task.FromResult((IReadOnlyList<DailyAggregate>)Array.Empty<DailyAggregate>());
}

sealed class FixedClock(DateTime utc) : IClock { public DateTime UtcNow => utc; }

public sealed class MonitorFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Ups3", "Server=dummy;Database=dummy;");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ISensorReadingRepository>();
            services.AddSingleton<ISensorReadingRepository>(new FakeRepo());
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(new FixedClock(new DateTime(2026, 6, 15, 16, 10, 0, DateTimeKind.Utc)));
        });
    }
}

public class ApiTests(MonitorFactory factory) : IClassFixture<MonitorFactory>
{
    [Fact]
    public async Task Latest_returns_status_json()
    {
        var res = await factory.CreateClient().GetAsync("/api/readings/latest");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<LatestDto>();
        Assert.Equal(19, body!.Reading.TemperatureC);
        Assert.False(body.IsStale);   // 10 min old < 30
    }

    [Fact]
    public async Task Series_with_preset_returns_points()
    {
        var res = await factory.CreateClient().GetAsync("/api/readings/series?range=24h");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var pts = await res.Content.ReadFromJsonAsync<List<SeriesPoint>>();
        Assert.Single(pts!);
    }

    [Fact]
    public async Task Series_with_bad_range_is_400()
    {
        var res = await factory.CreateClient().GetAsync("/api/readings/series?range=5y");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Health_reports_ok()
    {
        var res = await factory.CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);   // fresh -> Healthy
    }

    sealed record LatestDto(SensorReading Reading, bool IsStale);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/ClimaSense.Monitor.Tests --filter ApiTests`
Expected: FAIL first run if `Program` is not yet `public partial` — confirm Task 11 added `public partial class Program { }`. Otherwise FAIL on assertions until wiring is correct, then PASS.

- [ ] **Step 3: Run test to verify it passes**

Run: `dotnet test tests/ClimaSense.Monitor.Tests --filter ApiTests`
Expected: PASS (4 tests).

- [ ] **Step 4: Run the full suite**

Run: `dotnet test ClimaSense.sln`
Expected: PASS (integration tests SKIP unless `CLIMASENSE_UPS3_CONNECTION` is set).

- [ ] **Step 5: Commit**

```bash
git add tests/ClimaSense.Monitor.Tests/Endpoints/ApiTests.cs
git commit -m "test(api): endpoint + health integration tests via WebApplicationFactory"
```

---

### Task 13: Live page (layout, theme, charts, polling)

**Files:**
- Modify: `src/ClimaSense.Monitor/Program.cs` (add JSON string-enum converter)
- Modify: `src/ClimaSense.Monitor/Pages/Shared/_Layout.cshtml` (replace template)
- Modify: `src/ClimaSense.Monitor/wwwroot/css/site.css` (replace template)
- Create: `src/ClimaSense.Monitor/wwwroot/js/charts.js`
- Create: `src/ClimaSense.Monitor/wwwroot/js/live.js`
- Modify: `src/ClimaSense.Monitor/Pages/Index.cshtml` and `Index.cshtml.cs` (replace template)
- Create: `tests/ClimaSense.Monitor.Tests/Endpoints/PagesTests.cs`

- [ ] **Step 1: Serialize enums as names (UI keys CSS classes off band names)**

In `src/ClimaSense.Monitor/Program.cs`, add immediately before `var app = builder.Build();`:

```csharp
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
```

- [ ] **Step 2: Vendor Chart.js locally, then replace `Pages/Shared/_Layout.cshtml`**

Self-host Chart.js — avoids a public-CDN dependency on an internal IIS box (supply-chain / no-SRI risk) and works offline:

```bash
mkdir -p src/ClimaSense.Monitor/wwwroot/lib/chartjs
curl -fsSL https://cdn.jsdelivr.net/npm/chart.js@4.4.6/dist/chart.umd.min.js \
  -o src/ClimaSense.Monitor/wwwroot/lib/chartjs/chart.umd.min.js
```

Then write `_Layout.cshtml`:

```html
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>@ViewData["Title"] · ClimaSense</title>
    <link rel="stylesheet" href="~/css/site.css" asp-append-version="true" />
    <script src="~/lib/chartjs/chart.umd.min.js" asp-append-version="true"></script>
</head>
<body>
    <header>
        <a class="brand" href="/">ClimaSense</a>
        <nav><a href="/">Live</a><a href="/history">History</a></nav>
    </header>
    <main>@RenderBody()</main>
    @await RenderSectionAsync("Scripts", required: false)
</body>
</html>
```

- [ ] **Step 3: Replace `wwwroot/css/site.css`**

```css
:root { --bg:#0d1117; --panel:#161b22; --fg:#e6edf3; --muted:#8b949e; --green:#2ea043; --amber:#d29922; --red:#f85149; --line:#30363d; }
* { box-sizing:border-box; }
body { margin:0; background:var(--bg); color:var(--fg); font:15px/1.5 -apple-system,Segoe UI,Roboto,sans-serif; }
header { display:flex; align-items:center; gap:24px; padding:12px 20px; border-bottom:1px solid var(--line); }
.brand { font-weight:700; color:var(--fg); text-decoration:none; font-size:18px; }
nav a { color:var(--muted); text-decoration:none; margin-right:16px; }
nav a:hover { color:var(--fg); }
main { padding:20px; max-width:1100px; margin:0 auto; }
h1 { font-size:22px; }
.tiles { display:flex; gap:16px; flex-wrap:wrap; }
.tile { background:var(--panel); border:1px solid var(--line); border-radius:10px; padding:20px 24px; min-width:200px; }
.tile .v { font-size:42px; font-weight:700; }
.tile .l { color:var(--muted); text-transform:uppercase; font-size:12px; letter-spacing:.05em; }
.band-Recommended { color:var(--green); } .band-Allowable { color:var(--amber); } .band-OutOfRange { color:var(--red); }
.banner { background:#3d2b00; border:1px solid var(--amber); color:#ffd479; padding:10px 14px; border-radius:8px; margin:12px 0; display:none; }
.banner.show { display:block; }
.panel { background:var(--panel); border:1px solid var(--line); border-radius:10px; padding:16px; margin-top:16px; }
.ranges button { background:var(--panel); color:var(--fg); border:1px solid var(--line); border-radius:6px; padding:6px 12px; margin-right:6px; cursor:pointer; }
.ranges button.active { border-color:var(--green); color:var(--green); }
table { width:100%; border-collapse:collapse; }
th,td { text-align:left; padding:8px; border-bottom:1px solid var(--line); }
.heatmap { display:grid; grid-template-columns:repeat(auto-fill,12px); gap:2px; }
.heatmap .cell { width:12px; height:12px; border-radius:2px; background:#21262d; }
.muted { color:var(--muted); }
```

- [ ] **Step 4: Create `wwwroot/js/charts.js`**

```javascript
window.ClimaCharts = (function () {
  function lineChart(canvasId, labels, datasets) {
    return new Chart(document.getElementById(canvasId), {
      type: 'line',
      data: { labels, datasets },
      options: {
        responsive: true, interaction: { mode: 'index', intersect: false },
        scales: { x: { ticks: { color: '#8b949e' }, grid: { color: '#21262d' } },
                  y: { ticks: { color: '#8b949e' }, grid: { color: '#21262d' } } },
        plugins: { legend: { labels: { color: '#e6edf3' } } }
      }
    });
  }
  function ds(label, data, color) {
    return { label, data, borderColor: color, backgroundColor: color, tension: .25, pointRadius: 0, borderWidth: 2 };
  }
  return { lineChart, ds };
})();
```

- [ ] **Step 5: Create `wwwroot/js/live.js`**

```javascript
(function () {
  let chart = null;
  function setTile(id, value, band) { const el = document.getElementById(id); el.textContent = value; el.className = 'v band-' + band; }
  async function refresh() {
    try {
      const stale = document.getElementById('stale');
      const s = await fetch('/api/readings/latest').then(r => r.ok ? r.json() : null);
      if (!s) { stale.textContent = 'No data available.'; stale.classList.add('show'); return; }
      setTile('temp', s.reading.temperatureC + ' °C', s.tempBand);
      setTile('hum', s.reading.humidityPct + ' %', s.humidityBand);
      document.getElementById('updated').textContent = 'updated ' + s.minutesOld + ' min ago';
      stale.classList.toggle('show', s.isStale);
      stale.textContent = s.isStale ? ('Feed stale — last reading ' + s.minutesOld + ' min ago') : '';
      const pts = await fetch('/api/readings/series?range=24h').then(r => r.json());
      const labels = pts.map(p => new Date(p.bucketStartCet).toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' }));
      const t = pts.map(p => p.avgTemp), h = pts.map(p => p.avgHumidity);
      if (chart) { chart.data.labels = labels; chart.data.datasets[0].data = t; chart.data.datasets[1].data = h; chart.update(); }
      else chart = ClimaCharts.lineChart('liveChart', labels,
        [ClimaCharts.ds('Temp °C', t, '#58a6ff'), ClimaCharts.ds('Humidity %', h, '#3fb950')]);
    } catch (e) { console.error(e); }
  }
  refresh();
  setInterval(refresh, 60000);
})();
```

- [ ] **Step 6: Replace `Pages/Index.cshtml` and `Index.cshtml.cs`**

```html
@page
@{ ViewData["Title"] = "Live"; }
<h1>Live — UPS Room</h1>
<div id="stale" class="banner"></div>
<div class="tiles">
  <div class="tile"><div class="l">Temperature</div><div id="temp" class="v">—</div></div>
  <div class="tile"><div class="l">Humidity</div><div id="hum" class="v">—</div></div>
</div>
<p id="updated" class="muted">—</p>
<div class="panel"><canvas id="liveChart" height="120"></canvas></div>
@section Scripts {
  <script src="~/js/charts.js" asp-append-version="true"></script>
  <script src="~/js/live.js" asp-append-version="true"></script>
}
```

```csharp
// src/ClimaSense.Monitor/Pages/Index.cshtml.cs
using Microsoft.AspNetCore.Mvc.RazorPages;
namespace ClimaSense.Monitor.Pages;
public class IndexModel : PageModel { public void OnGet() { } }
```

- [ ] **Step 7: Smoke test the page renders**

```csharp
// tests/ClimaSense.Monitor.Tests/Endpoints/PagesTests.cs
using System.Net;
using System.Threading.Tasks;
using Xunit;

namespace ClimaSense.Monitor.Tests.Endpoints;

public class PagesTests(MonitorFactory factory) : IClassFixture<MonitorFactory>
{
    [Fact]
    public async Task Live_page_returns_200()
        => Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().GetAsync("/")).StatusCode);
}
```

- [ ] **Step 8: Run + commit**

Run: `dotnet test tests/ClimaSense.Monitor.Tests --filter PagesTests`
Expected: PASS.

```bash
git add src/ClimaSense.Monitor/Program.cs src/ClimaSense.Monitor/Pages src/ClimaSense.Monitor/wwwroot tests/ClimaSense.Monitor.Tests/Endpoints/PagesTests.cs
git commit -m "feat(ui): live page with tiles, freshness banner, 24h chart"
```

---

### Task 14: History page (range picker, charts, heatmap, excursions)

**Files:**
- Create: `src/ClimaSense.Monitor/Pages/History.cshtml` and `History.cshtml.cs`
- Create: `src/ClimaSense.Monitor/wwwroot/js/history.js`
- Modify: `tests/ClimaSense.Monitor.Tests/Endpoints/PagesTests.cs` (add `/history`)

- [ ] **Step 1: Create `Pages/History.cshtml` and `History.cshtml.cs`**

```html
@page
@{ ViewData["Title"] = "History"; }
<h1>History</h1>
<div class="ranges">
  <button data-r="24h">24h</button><button data-r="7d" class="active">7d</button>
  <button data-r="30d">30d</button><button data-r="90d">90d</button><button data-r="1y">1y</button>
</div>
<div class="panel"><canvas id="histChart" height="120"></canvas></div>
<div class="panel"><h3>Daily heatmap (avg °C)</h3><div id="heatmap" class="heatmap"></div></div>
<div class="panel"><h3>Excursions</h3>
  <table id="excursions">
    <thead><tr><th>Metric</th><th>Start</th><th>End</th><th>Duration</th><th>Peak</th></tr></thead>
    <tbody></tbody>
  </table>
</div>
@section Scripts {
  <script src="~/js/charts.js" asp-append-version="true"></script>
  <script src="~/js/history.js" asp-append-version="true"></script>
}
```

```csharp
// src/ClimaSense.Monitor/Pages/History.cshtml.cs
using Microsoft.AspNetCore.Mvc.RazorPages;
namespace ClimaSense.Monitor.Pages;
public class HistoryModel : PageModel { public void OnGet() { } }
```

- [ ] **Step 2: Create `wwwroot/js/history.js`**

```javascript
(function () {
  let chart = null;
  async function load(range) {
    document.querySelectorAll('.ranges button').forEach(b => b.classList.toggle('active', b.dataset.r === range));
    const [series, daily, excursions] = await Promise.all([
      fetch('/api/readings/series?range=' + range).then(r => r.json()),
      fetch('/api/readings/daily?range=' + range).then(r => r.json()),
      fetch('/api/readings/excursions?range=' + range).then(r => r.json()),
    ]);
    renderChart(series); renderHeatmap(daily); renderExcursions(excursions);
  }
  function renderChart(pts) {
    const labels = pts.map(p => new Date(p.bucketStartCet).toLocaleString('en-GB'));
    const t = pts.map(p => p.avgTemp), h = pts.map(p => p.avgHumidity);
    if (chart) { chart.data.labels = labels; chart.data.datasets[0].data = t; chart.data.datasets[1].data = h; chart.update(); }
    else chart = ClimaCharts.lineChart('histChart', labels,
      [ClimaCharts.ds('Temp °C', t, '#58a6ff'), ClimaCharts.ds('Humidity %', h, '#3fb950')]);
  }
  function renderHeatmap(daily) {
    const grid = document.getElementById('heatmap'); grid.innerHTML = '';
    if (!daily.length) return;
    const temps = daily.map(d => d.avgTemp), min = Math.min(...temps), max = Math.max(...temps);
    daily.forEach(d => {
      const cell = document.createElement('div'); cell.className = 'cell';
      const f = (d.avgTemp - min) / (max - min || 1);
      cell.style.background = `rgb(${Math.round(40 + f * 200)},80,${Math.round(120 - f * 60)})`;
      cell.title = d.dateCet + ': ' + d.avgTemp.toFixed(1) + ' °C';
      grid.appendChild(cell);
    });
  }
  function renderExcursions(ex) {
    const tb = document.querySelector('#excursions tbody'); tb.innerHTML = '';
    if (!ex.length) { tb.innerHTML = '<tr><td colspan="5" class="muted">No excursions in range.</td></tr>'; return; }
    ex.forEach(e => {
      const tr = document.createElement('tr');
      tr.innerHTML = `<td>${e.metric}</td><td>${new Date(e.startCet).toLocaleString('en-GB')}</td>`
        + `<td>${new Date(e.endCet).toLocaleString('en-GB')}</td><td>${e.durationMinutes} min</td>`
        + `<td class="band-${e.band}">${e.peak.toFixed(1)}</td>`;
      tb.appendChild(tr);
    });
  }
  document.querySelectorAll('.ranges button').forEach(b => b.addEventListener('click', () => load(b.dataset.r)));
  load('7d');
})();
```

- [ ] **Step 3: Extend the smoke test (full file)**

```csharp
// tests/ClimaSense.Monitor.Tests/Endpoints/PagesTests.cs
using System.Net;
using System.Threading.Tasks;
using Xunit;

namespace ClimaSense.Monitor.Tests.Endpoints;

public class PagesTests(MonitorFactory factory) : IClassFixture<MonitorFactory>
{
    [Theory]
    [InlineData("/")]
    [InlineData("/history")]
    public async Task Page_returns_200(string url)
        => Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().GetAsync(url)).StatusCode);
}
```

- [ ] **Step 4: Run + commit**

Run: `dotnet test tests/ClimaSense.Monitor.Tests --filter PagesTests`
Expected: PASS (2 cases).

```bash
git add src/ClimaSense.Monitor/Pages/History.cshtml src/ClimaSense.Monitor/Pages/History.cshtml.cs src/ClimaSense.Monitor/wwwroot/js/history.js tests/ClimaSense.Monitor.Tests/Endpoints/PagesTests.cs
git commit -m "feat(ui): history page with range picker, heatmap, excursions"
```

- [ ] **Step 5: Manual smoke run (optional, needs DB)**

```bash
export CLIMASENSE_UPS3_CONNECTION='Server=util02.lab.local,1433;Database=ups3;User ID=<your-db-user>;Password=<your-db-password>;Encrypt=True;TrustServerCertificate=True'
dotnet run --project src/ClimaSense.Monitor
```
Open `http://localhost:5000` (Live) and `/history`. Confirm tiles update and charts render.

---

### Task 15: SQL scripts + deployment doc

**Files:**
- Create: `scripts/ups3-index.sql`
- Create: `scripts/climasense_ro.sql`
- Create: `docs/DEPLOY.md`

- [ ] **Step 1: Create `scripts/ups3-index.sql`**

```sql
-- Covering index for ClimaSense range/aggregation queries on ups3.dbo.tbl_sensor_data.
-- Run once as a privileged login (e.g. <your-db-user>). Idempotent.
IF NOT EXISTS (SELECT 1 FROM sys.indexes
              WHERE name = 'IX_tbl_sensor_data_sensor_dateTime'
                AND object_id = OBJECT_ID('dbo.tbl_sensor_data'))
    CREATE NONCLUSTERED INDEX IX_tbl_sensor_data_sensor_dateTime
        ON dbo.tbl_sensor_data (sensor_dateTime)
        INCLUDE (temperature, humidity);
```

- [ ] **Step 2: Create `scripts/climasense_ro.sql`**

```sql
-- Optional: least-privilege login to replace <your-db-user> in the connection string.
-- Change the password before running. Idempotent.
USE master;
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = 'climasense_ro')
    CREATE LOGIN [climasense_ro] WITH PASSWORD = 'CHANGE_ME_Strong#2026', CHECK_POLICY = ON;
GO
USE ups3;
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = 'climasense_ro')
    CREATE USER [climasense_ro] FOR LOGIN [climasense_ro];
GRANT SELECT ON dbo.tbl_sensor_data TO [climasense_ro];
GO
```

- [ ] **Step 3: Create `docs/DEPLOY.md`**

```markdown
# Deploying ClimaSense.Monitor to IIS

## Prerequisites (Windows server)
- Install the **ASP.NET Core 10 Hosting Bundle** (ANCM + runtime), then `iisreset`.

## Publish (from the Mac)
    dotnet publish src/ClimaSense.Monitor -c Release -o ./publish
Copy `./publish` to the server, e.g. `C:\inetpub\climasense`.

## IIS
1. Create an app pool with **.NET CLR version = No Managed Code**.
2. Create a site/app pointing at the publish folder, using that pool; add an HTTPS binding.
3. Set the connection string (machine or site env var):
   `setx CLIMASENSE_UPS3_CONNECTION "Server=util02.lab.local,1433;Database=ups3;User ID=<your-db-user>;Password=<your-db-password>;Encrypt=True;TrustServerCertificate=True" /M`
   (or add it under `<aspNetCore><environmentVariables>` in `web.config`, and consider encrypting it).
4. Browse the site; check `/health` returns 200.

## Optional hardening
- Run `scripts/ups3-index.sql` (query performance).
- Run `scripts/climasense_ro.sql` and switch the connection string to `climasense_ro` (drops sysadmin usage).
```

- [ ] **Step 4: Commit**

```bash
git add scripts/ups3-index.sql scripts/climasense_ro.sql docs/DEPLOY.md
git commit -m "docs: ups3 index, least-priv login, IIS deploy guide"
```

---

### Task 16: Retire the old stack (destructive — review first)

**Files (delete):** `docker-compose.yml`, `.env`, `.env.example`, `src/ClimaSense.ML/`, `src/ClimaSense.Web/`, `contracts/`, `scripts/init-db.sql`, `scripts/regen-contracts.sh`, `scripts/gen-smoke-fixture.py`, `scripts/smoke_test.sh`, `scripts/derive_pattern_thresholds.py`, `sensor_data.csv`, `tbl_sensor_data.csv`

> Review this list before running. The `docs/adr/`, `SLICE-*-NOTES.md`, and `README.md` are **kept** (history + to be rewritten separately). Do this only after Tasks 1–15 are green.

- [ ] **Step 1: Remove old projects from the solution**

```bash
dotnet sln ClimaSense.sln remove src/ClimaSense.Web src/ClimaSense.ML 2>/dev/null || true
```

- [ ] **Step 2: Delete obsolete artifacts**

```bash
git rm -r --ignore-unmatch \
  docker-compose.yml .env .env.example \
  src/ClimaSense.ML src/ClimaSense.Web contracts \
  scripts/init-db.sql scripts/regen-contracts.sh scripts/gen-smoke-fixture.py \
  scripts/smoke_test.sh scripts/derive_pattern_thresholds.py \
  sensor_data.csv tbl_sensor_data.csv
```

- [ ] **Step 3: Verify the solution still builds and tests pass**

Run: `dotnet build ClimaSense.sln && dotnet test ClimaSense.sln`
Expected: Build succeeded; tests PASS (integration SKIP unless connection set).

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "chore: retire containerized Python/SQL stack; ups3 monitor is the app"
```

---

## Spec Coverage Map (author self-review)

| Spec section | Implemented by |
|---|---|
| §2 data source / schema | Task 8 (SQL repo), Task 5 (DTOs) |
| §3 scope: live status | Tasks 7, 11, 13 |
| §3 scope: trend charts | Tasks 8, 13 |
| §3 scope: history aggregates + heatmap | Tasks 8, 14 |
| §3 scope: excursions | Tasks 6, 7, 14 |
| §3 scope: health endpoint | Tasks 11, 12 |
| §4 auth (<your-db-user>) + secret in env | Tasks 8, 11, 15 |
| §4 least-priv hardening | Task 15 (`climasense_ro.sql`) |
| §5 architecture (.NET 10, Razor + Minimal API, Dapper, IIS) | Tasks 1, 10, 11, 13–15 |
| §6 server-side aggregation + bucket selection | Tasks 5, 8 |
| §6 index + memory cache | Tasks 9, 15 |
| §7 domain model + bands + freshness | Tasks 2–4, 7 |
| §8 CET (`Europe/Berlin`) handling | Task 4, used in 7/10 |
| §9 pages/UX | Tasks 13, 14 |
| §10 endpoints + validation | Tasks 10, 12 |
| §11 error handling | Tasks 10 (400s), 11 (health/exception handler) |
| §12 testing (unit + opt-in integration) | Tasks 2–10, 12 |
| §13 structure | Task 1 + all |
| §14 deployment | Task 15 (`DEPLOY.md`) |
| §15 retire old stack | Task 16 |

**Self-review notes:** no `TBD`/placeholder steps; types are consistent across tasks (`SensorReading`, `SeriesPoint`, `DailyAggregate`, `Excursion`, `LatestStatus`, `ISensorReadingRepository` signatures match their call sites; `BandEvaluator`/`BucketSelector`/`ExcursionDetector`/`CetZone` names used identically in tests and consumers). The JSON string-enum converter (Task 13 Step 1) is required for the UI's `band-<Name>` CSS classes and the excursion table.


