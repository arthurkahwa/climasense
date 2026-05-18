// SPDX-License-Identifier: MIT
//
// SqlComfortBudgetFetcher — production adapter for
// `ComfortBudgetFetcher`. Implements the three deterministic SQL
// aggregations per slice 10 (issue #12 + ADR-0006):
//
//   1. Hours outside zone — `SELECT COUNT(*) FROM
//      dbo.fv_comfortscores_at_cursor(@asOf) WHERE BucketTime BETWEEN
//      @start AND @end AND Score < @threshold`.
//   2. Worst calendar cell — `SELECT TOP 1 ... FROM
//      dbo.fv_dayprofiles_at_cursor(@asOf) WHERE [Date] BETWEEN
//      @startDate AND @endDate ORDER BY MeanResidual ASC, [Date] DESC`
//      (tie-breaker: most-recent date wins; documented in PR notes).
//   3. 7-day trend — `SELECT DATE_BUCKET(DAY, 1, BucketTime) AS Day,
//      MIN(Score), MAX(Score), AVG(Score), COUNT(*) ... FROM
//      dbo.fv_comfortscores_at_cursor(@asOf) WHERE BucketTime BETWEEN
//      @start AND @end GROUP BY DATE_BUCKET(DAY, 1, BucketTime)`.
//
// Cursor-clipping is enforced by the schema — every aggregation reads
// through the inline TVFs `dbo.fv_comfortscores_at_cursor(@asOf)` and
// `dbo.fv_dayprofiles_at_cursor(@asOf)` defined in init-db.sql §3.3/3.4.
// Caller-side `WHERE BucketTime <= cursor` is intentionally absent —
// the TVF *is* the clip. The aggregations additionally filter on
// `BucketTime BETWEEN @start AND @end` (the 7-day window) and
// `[Date] BETWEEN @startDate AND @endDate` (the worst-cell window).

#nullable enable

using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ClimaSense.Web.Comfort;

public sealed class SqlComfortBudgetFetcher
{
    private readonly string _connectionString;
    private readonly ILogger<SqlComfortBudgetFetcher> _logger;

    public SqlComfortBudgetFetcher(
        IConfiguration configuration,
        ILogger<SqlComfortBudgetFetcher> logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);
        _connectionString = BuildConnectionString(configuration);
        _logger = logger;
    }

    /// <summary>
    /// Aggregation 1 — hours outside zone. Counts rows in the window
    /// whose <c>Score</c> falls below the discomfort threshold. The
    /// `Score` column is `DECIMAL(5,2)` so the comparison is exact. The
    /// window is half-open at the upper bound only on the caller side
    /// (`@end == cursor.AsOf`); SQL uses an inclusive `BETWEEN` because
    /// `BucketTime` is hourly-aligned and the cursor lands at a
    /// finer-than-bucket resolution.
    /// </summary>
    public const string HoursOutsideZoneSql = """
        SELECT COUNT(*)
          FROM dbo.fv_comfortscores_at_cursor(@asOf)
         WHERE BucketTime >= @start
           AND BucketTime <= @end
           AND Score < @threshold;
        """;

    /// <summary>
    /// Aggregation 2 — worst calendar cell in the window. Tie-breaker:
    /// most-recent <c>Date</c> wins when two days share the same
    /// `MeanResidual` (judgment call per #12 spec, which leaves
    /// tie-breaking unspecified). Falls back to first by
    /// <c>DayOfWeek</c> when `Date` also ties (impossible given
    /// `UQ_DayProfiles_Date`, but defensive nonetheless).
    /// </summary>
    public const string WorstCellSql = """
        SELECT TOP 1 [Date], DayOfWeek, MeanResidual, MaxAbsZscore, Pattern
          FROM dbo.fv_dayprofiles_at_cursor(@asOf)
         WHERE [Date] >= @startDate
           AND [Date] <= @endDate
         ORDER BY MeanResidual ASC, [Date] DESC, DayOfWeek ASC;
        """;

    /// <summary>
    /// Aggregation 3 — 7-day comfort trend. One row per calendar day
    /// where at least one comfort score exists. `DATE_BUCKET(DAY, 1,
    /// BucketTime)` returns the day's midnight UTC; the caller projects
    /// it to <see cref="DateOnly"/>. Empty days are omitted (the
    /// dashboard interpolates gaps visually).
    /// </summary>
    public const string TrendSql = """
        SELECT DATE_BUCKET(DAY, 1, BucketTime)                      AS Day,
               CAST(MIN(Score) AS DECIMAL(5, 2))                    AS MinScore,
               CAST(MAX(Score) AS DECIMAL(5, 2))                    AS MaxScore,
               CAST(AVG(CAST(Score AS DECIMAL(8, 4))) AS DECIMAL(5, 2)) AS MeanScore,
               COUNT(*)                                              AS SampleCount
          FROM dbo.fv_comfortscores_at_cursor(@asOf)
         WHERE BucketTime >= @start
           AND BucketTime <= @end
         GROUP BY DATE_BUCKET(DAY, 1, BucketTime)
         ORDER BY Day ASC;
        """;

    /// <summary>
    /// Run all three aggregations against the same connection in
    /// sequence. One round-trip per query, which is cheap because each
    /// query is a single index scan over the cursor-clipped TVF and the
    /// total row count in a 7-day window is bounded
    /// (≤ 24 × 7 = 168 comfort rows; ≤ 7 day-profile rows). Total
    /// budget well under the 500 ms AC.
    /// </summary>
    public async Task<ComfortBudgetDto> FetchAsync(
        DateTime asOf,
        DateTime windowStart,
        DateTime windowEnd,
        int windowDays,
        double threshold,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            var hoursOutsideZone = await ReadHoursOutsideZoneAsync(
                connection, asOf, windowStart, windowEnd, threshold, cancellationToken)
                .ConfigureAwait(false);

            var worstCell = await ReadWorstCellAsync(
                connection,
                asOf,
                DateOnly.FromDateTime(windowStart),
                DateOnly.FromDateTime(windowEnd),
                cancellationToken)
                .ConfigureAwait(false);

            var trend = await ReadTrendAsync(
                connection, asOf, windowStart, windowEnd, cancellationToken)
                .ConfigureAwait(false);

            return new ComfortBudgetDto(
                HoursOutsideZone: hoursOutsideZone,
                Threshold: threshold,
                WindowDays: windowDays,
                WindowStart: DateTime.SpecifyKind(windowStart, DateTimeKind.Utc),
                WindowEnd: DateTime.SpecifyKind(windowEnd, DateTimeKind.Utc),
                WorstCell: worstCell,
                Trend: trend);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SqlException ex)
        {
            _logger.LogWarning(ex, "ComfortBudgetReadService: SQL read failed");
            throw;
        }
    }

    private static async Task<int> ReadHoursOutsideZoneAsync(
        SqlConnection connection,
        DateTime asOf,
        DateTime start,
        DateTime end,
        double threshold,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = HoursOutsideZoneSql;
        command.CommandType = CommandType.Text;
        AddDateTime2(command, "@asOf", asOf);
        AddDateTime2(command, "@start", start);
        AddDateTime2(command, "@end", end);

        var thresholdParam = command.CreateParameter();
        thresholdParam.ParameterName = "@threshold";
        thresholdParam.SqlDbType = SqlDbType.Decimal;
        thresholdParam.Precision = 5;
        thresholdParam.Scale = 2;
        thresholdParam.Value = (decimal)threshold;
        command.Parameters.Add(thresholdParam);

        var raw = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return raw is null ? 0 : Convert.ToInt32(raw);
    }

    private static async Task<WorstCalendarCellDto?> ReadWorstCellAsync(
        SqlConnection connection,
        DateTime asOf,
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = WorstCellSql;
        command.CommandType = CommandType.Text;
        AddDateTime2(command, "@asOf", asOf);
        AddDate(command, "@startDate", startDate);
        AddDate(command, "@endDate", endDate);

        await using var reader = await command
            .ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken)
            .ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        // Schema (init-db.sql §2.4): same columns as DayProfileDto, minus
        // ComputedAt (we don't surface it in the budget payload — the
        // dashboard cares about the cell, not when it was computed).
        var date = DateOnly.FromDateTime(reader.GetDateTime(0));
        var dayOfWeek = reader.GetByte(1);
        var meanResidual = (double)reader.GetDecimal(2);
        var maxAbsZscore = (double)reader.GetDecimal(3);
        var pattern = reader.GetString(4);
        return new WorstCalendarCellDto(
            Date: date,
            DayOfWeek: dayOfWeek,
            MeanResidual: meanResidual,
            MaxAbsZscore: maxAbsZscore,
            Pattern: pattern);
    }

    private static async Task<IReadOnlyList<ComfortTrendPointDto>> ReadTrendAsync(
        SqlConnection connection,
        DateTime asOf,
        DateTime start,
        DateTime end,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = TrendSql;
        command.CommandType = CommandType.Text;
        AddDateTime2(command, "@asOf", asOf);
        AddDateTime2(command, "@start", start);
        AddDateTime2(command, "@end", end);

        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        var rows = new List<ComfortTrendPointDto>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // DATE_BUCKET(DAY, 1, BucketTime) returns DATETIME2; we
            // project to DateOnly because the dashboard only needs the
            // calendar day.
            var day = DateOnly.FromDateTime(reader.GetDateTime(0));
            var minScore = (double)reader.GetDecimal(1);
            var maxScore = (double)reader.GetDecimal(2);
            var meanScore = (double)reader.GetDecimal(3);
            var sampleCount = reader.GetInt32(4);
            rows.Add(new ComfortTrendPointDto(
                Day: day,
                MinScore: minScore,
                MaxScore: maxScore,
                MeanScore: meanScore,
                SampleCount: sampleCount));
        }
        return rows;
    }

    private static void AddDateTime2(SqlCommand command, string name, DateTime value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.SqlDbType = SqlDbType.DateTime2;
        p.Value = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
        command.Parameters.Add(p);
    }

    private static void AddDate(SqlCommand command, string name, DateOnly value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.SqlDbType = SqlDbType.Date;
        p.Value = value.ToDateTime(TimeOnly.MinValue);
        command.Parameters.Add(p);
    }

    private static string BuildConnectionString(IConfiguration config)
    {
        var host = config["CLIMASENSE_DB_HOST"] ?? "db";
        var port = config["CLIMASENSE_DB_PORT"] ?? "1433";
        var name = config["CLIMASENSE_DB_NAME"] ?? "ClimaSense";
        var user = config["CLIMASENSE_DB_USER"] ?? "sa";
        var pwd = config["CLIMASENSE_DB_PASSWORD"] ?? string.Empty;

        var b = new SqlConnectionStringBuilder
        {
            DataSource = $"{host},{port}",
            InitialCatalog = name,
            UserID = user,
            Password = pwd,
            Encrypt = true,
            TrustServerCertificate = true,
            ConnectTimeout = 5,
            CommandTimeout = 5,
        };
        return b.ConnectionString;
    }
}
