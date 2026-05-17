// SPDX-License-Identifier: MIT
//
// SqlRangeFetcher — production adapter for `RangeFetcher` +
// `HeatmapFetcher` (see RangeQueryService.cs).
//
// Owns:
//   * Connection-string construction from `IConfiguration` (mirrors
//     the slice-3 SqlLatestReadingFetcher; the duplication is
//     intentional — the seam between Razor wiring and SQL access is
//     "one fetcher per concern" and these are distinct concerns).
//   * The DATE_BUCKET aggregation SQL for each granularity, plus the
//     un-aggregated raw `SELECT * FROM SensorReadings WHERE ...`
//     for `RangeBucket.Raw`.
//   * Reading rows back into `BucketedReading` / `HeatmapCell` records
//     and coercing `DECIMAL(6, 3)` columns to `double`.
//
// SQL strategy:
//   For Hour/Day/Week:
//     SELECT DATE_BUCKET(@width, 1, ReadingTime) AS BucketTime,
//            COUNT(*)         AS SampleCount,
//            AVG(Temperature) AS TemperatureMean,
//            MIN(Temperature) AS TemperatureMin,
//            ...
//     FROM   SensorReadings
//     WHERE  ReadingTime >= @start AND ReadingTime <= @end
//        AND ReadingTime <= @asOf
//     GROUP BY DATE_BUCKET(...)
//     ORDER BY BucketTime;
//   For Raw:
//     SELECT ReadingTime, Temperature, Humidity
//     FROM   SensorReadings
//     WHERE  ReadingTime >= @start AND ReadingTime <= @end
//        AND ReadingTime <= @asOf
//     ORDER BY ReadingTime;
//
// Why parameter binding for `@width`:
//   SQL Server's `DATE_BUCKET` first argument is a *datepart* (compiler
//   literal), not a runtime value — you cannot bind `'HOUR'` as a
//   parameter. We branch on the bucket enum and emit one of the three
//   pre-compiled query strings. The `start` / `end` / `asOf`
//   parameters are still bound; the bucket selector is a discrete
//   choice over a closed set so the SQL-injection surface is zero.

#nullable enable

using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ClimaSense.Web.Readings;

public sealed class SqlRangeFetcher
{
    private readonly string _connectionString;
    private readonly ILogger<SqlRangeFetcher> _logger;

    public SqlRangeFetcher(
        IConfiguration configuration,
        ILogger<SqlRangeFetcher> logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);
        _connectionString = BuildConnectionString(configuration);
        _logger = logger;
    }

    /// <summary>
    /// Adapter for <see cref="RangeFetcher"/>. Branches on
    /// <paramref name="bucket"/>: raw vs aggregated → distinct SQL.
    /// </summary>
    public async Task<IReadOnlyList<BucketedReading>> FetchRangeAsync(
        RangeBucket bucket,
        DateTime start,
        DateTime end,
        DateTime asOf,
        CancellationToken cancellationToken)
    {
        var rows = new List<BucketedReading>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandType = CommandType.Text;
        command.CommandText = bucket == RangeBucket.Raw
            ? BuildRawSql()
            : BuildAggregatedSql(bucket);

        AddDateTimeParam(command, "@start", start);
        AddDateTimeParam(command, "@end", end);
        AddDateTimeParam(command, "@asOf", asOf);

        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (bucket == RangeBucket.Raw)
            {
                var rt = DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc);
                var t = (double)reader.GetDecimal(1);
                var h = (double)reader.GetDecimal(2);
                rows.Add(new BucketedReading(
                    BucketTime: rt,
                    SampleCount: 1,
                    TemperatureMean: t,
                    TemperatureMin: t,
                    TemperatureMax: t,
                    HumidityMean: h,
                    HumidityMin: h,
                    HumidityMax: h));
            }
            else
            {
                var bt = DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc);
                var count = reader.GetInt32(1);
                rows.Add(new BucketedReading(
                    BucketTime: bt,
                    SampleCount: count,
                    TemperatureMean: AsNullableDouble(reader, 2),
                    TemperatureMin: AsNullableDouble(reader, 3),
                    TemperatureMax: AsNullableDouble(reader, 4),
                    HumidityMean: AsNullableDouble(reader, 5),
                    HumidityMin: AsNullableDouble(reader, 6),
                    HumidityMax: AsNullableDouble(reader, 7)));
            }
        }

        return rows;
    }

    /// <summary>
    /// Adapter for <see cref="HeatmapFetcher"/>. Aggregates by
    /// <c>DATE_BUCKET(DAY, 1, ReadingTime)</c> over the year window and
    /// returns one row per populated day. Empty days are filled in by
    /// <see cref="RangeQueryService.GetHeatmapAsync"/>.
    /// </summary>
    public async Task<IReadOnlyList<HeatmapCell>> FetchHeatmapAsync(
        DateTime yearStart,
        DateTime yearEnd,
        DateTime asOf,
        CancellationToken cancellationToken)
    {
        // Heatmap end is exclusive (next year's Jan 1). We pass it as
        // a < bound so the last day of the previous year never bleeds
        // into the next year's heatmap.
        const string sql = """
            SELECT  CAST(DATE_BUCKET(DAY, 1, ReadingTime) AS DATETIME2(3)) AS BucketDate,
                    COUNT(*)         AS SampleCount,
                    AVG(Temperature) AS TemperatureMean
              FROM  dbo.SensorReadings
             WHERE  ReadingTime >= @start
               AND  ReadingTime <  @end
               AND  ReadingTime <= @asOf
             GROUP BY DATE_BUCKET(DAY, 1, ReadingTime)
             ORDER BY BucketDate;
            """;

        var cells = new List<HeatmapCell>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandType = CommandType.Text;
        command.CommandText = sql;
        AddDateTimeParam(command, "@start", yearStart);
        AddDateTimeParam(command, "@end", yearEnd);
        AddDateTimeParam(command, "@asOf", asOf);

        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var bt = reader.GetDateTime(0);
            var date = DateOnly.FromDateTime(bt);
            var count = reader.GetInt32(1);
            cells.Add(new HeatmapCell(
                Date: date,
                SampleCount: count,
                TemperatureMean: AsNullableDouble(reader, 2)));
        }

        return cells;
    }

    // -----------------------------------------------------------------
    // SQL builders. Held as static methods + visible to the test
    // project (via InternalsVisibleTo would be over-broad, so these
    // are `public`) so the golden-string tests can lock the exact
    // emitted SQL.
    // -----------------------------------------------------------------

    /// <summary>The cursor-clipped raw range query.</summary>
    public static string BuildRawSql() => """
        SELECT  ReadingTime,
                Temperature,
                Humidity
          FROM  dbo.SensorReadings
         WHERE  ReadingTime >= @start
           AND  ReadingTime <= @end
           AND  ReadingTime <= @asOf
         ORDER BY ReadingTime;
        """;

    /// <summary>
    /// The cursor-clipped aggregated range query for
    /// <paramref name="bucket"/>. Throws for <see cref="RangeBucket.Raw"/>.
    /// </summary>
    public static string BuildAggregatedSql(RangeBucket bucket)
    {
        var width = bucket.DateBucketWidth();
        // The DATE_BUCKET expression is repeated in SELECT and GROUP BY
        // because SQL Server cannot group by a column alias. Inlining the
        // string keeps the query plan predictable.
        return $"""
            SELECT  CAST(DATE_BUCKET({width}, 1, ReadingTime) AS DATETIME2(3)) AS BucketTime,
                    COUNT(*)         AS SampleCount,
                    AVG(Temperature) AS TemperatureMean,
                    MIN(Temperature) AS TemperatureMin,
                    MAX(Temperature) AS TemperatureMax,
                    AVG(Humidity)    AS HumidityMean,
                    MIN(Humidity)    AS HumidityMin,
                    MAX(Humidity)    AS HumidityMax
              FROM  dbo.SensorReadings
             WHERE  ReadingTime >= @start
               AND  ReadingTime <= @end
               AND  ReadingTime <= @asOf
             GROUP BY DATE_BUCKET({width}, 1, ReadingTime)
             ORDER BY BucketTime;
            """;
    }

    private static double? AsNullableDouble(SqlDataReader reader, int index)
    {
        if (reader.IsDBNull(index))
        {
            return null;
        }
        // AVG/MIN/MAX over DECIMAL(6, 3) returns DECIMAL; coerce to double.
        return (double)reader.GetDecimal(index);
    }

    private static void AddDateTimeParam(SqlCommand command, string name, DateTime value)
    {
        var param = command.CreateParameter();
        param.ParameterName = name;
        param.SqlDbType = SqlDbType.DateTime2;
        param.Value = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
        command.Parameters.Add(param);
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
            // Range scans over 1+ year of data can take a few seconds on
            // a single-core container, so allow more headroom than the
            // 5-second budget used by the single-row latest read.
            CommandTimeout = 30,
        };
        return b.ConnectionString;
    }
}
