// SPDX-License-Identifier: MIT
//
// SqlAnomalyFetcher — production adapter for `LatestAnomalyFetcher`
// and `AnomalyRangeFetcher`.
//
// Both reads go through the inline TVF
// `dbo.fv_anomalies_at_cursor(@asOf)` defined in `init-db.sql §3.2`
// so cursor-clipping is enforced by the schema, not by caller
// discipline. The TVF projects `dbo.Anomalies` with
// `WHERE DetectedAt <= @asOf`.
//
// `LatestSql` returns the row with the largest `DetectedAt` (TOP 1
// ORDER BY DetectedAt DESC). `RangeSql` returns rows in [start, end]
// with optional `AnomalyType` filter, ordered by `ReadingTime DESC`
// so the dashboard's "most recent first" listing is preserved.

#nullable enable

using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ClimaSense.Web.Anomalies;

public sealed class SqlAnomalyFetcher
{
    private readonly string _connectionString;
    private readonly ILogger<SqlAnomalyFetcher> _logger;

    public SqlAnomalyFetcher(
        IConfiguration configuration,
        ILogger<SqlAnomalyFetcher> logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);
        _connectionString = BuildConnectionString(configuration);
        _logger = logger;
    }

    /// <summary>
    /// SQL pinned by golden-string tests so cursor-clipping via the
    /// TVF can't silently change shape. Returns at most one row:
    /// the latest anomaly visible at the cursor.
    /// </summary>
    public const string LatestSql = """
        SELECT TOP 1 AnomalyType, ReadingTime, Severity, Description, DetectedAt
          FROM dbo.fv_anomalies_at_cursor(@asOf)
         ORDER BY DetectedAt DESC;
        """;

    /// <summary>
    /// SQL for the range read. The <c>@anomalyTypeFilter</c> parameter
    /// acts as a null-safe filter: when <c>NULL</c> the WHERE clause
    /// matches every row; when non-null it restricts to a single type.
    /// </summary>
    public const string RangeSql = """
        SELECT AnomalyType, ReadingTime, Severity, Description, DetectedAt
          FROM dbo.fv_anomalies_at_cursor(@asOf)
         WHERE ReadingTime >= @start
           AND ReadingTime <= @end
           AND (@anomalyTypeFilter IS NULL OR AnomalyType = @anomalyTypeFilter)
         ORDER BY ReadingTime DESC;
        """;

    public async Task<LatestAnomalyDto?> FetchLatestAsync(
        DateTime asOf,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = LatestSql;
            command.CommandType = CommandType.Text;
            AddDateTime2(command, "@asOf", asOf);

            await using var reader = await command
                .ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken)
                .ConfigureAwait(false);

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            return ReadRow(reader);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SqlException ex)
        {
            _logger.LogWarning(ex, "AnomalyReadService: latest SQL read failed");
            throw;
        }
    }

    public async Task<IReadOnlyList<LatestAnomalyDto>> FetchRangeAsync(
        DateTime asOf,
        DateTime start,
        DateTime end,
        string? anomalyType,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = RangeSql;
            command.CommandType = CommandType.Text;
            AddDateTime2(command, "@asOf", asOf);
            AddDateTime2(command, "@start", start);
            AddDateTime2(command, "@end", end);

            var typeParam = command.CreateParameter();
            typeParam.ParameterName = "@anomalyTypeFilter";
            typeParam.SqlDbType = SqlDbType.VarChar;
            typeParam.Size = 32;
            typeParam.Value = (object?)anomalyType ?? DBNull.Value;
            command.Parameters.Add(typeParam);

            await using var reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            var rows = new List<LatestAnomalyDto>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(ReadRow(reader));
            }
            return rows;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SqlException ex)
        {
            _logger.LogWarning(ex, "AnomalyReadService: range SQL read failed");
            throw;
        }
    }

    private static void AddDateTime2(SqlCommand command, string name, DateTime value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.SqlDbType = SqlDbType.DateTime2;
        p.Value = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
        command.Parameters.Add(p);
    }

    private static LatestAnomalyDto ReadRow(SqlDataReader reader)
    {
        // Schema (init-db.sql §2.2):
        //   AnomalyType VARCHAR(32) NOT NULL
        //   ReadingTime DATETIME2(3) NOT NULL
        //   Severity    DECIMAL(8, 4) NOT NULL
        //   Description NVARCHAR(512) NULL
        //   DetectedAt  DATETIME2(3) NOT NULL DEFAULT (SYSUTCDATETIME())
        var anomalyType = reader.GetString(0);
        var readingTime = DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc);
        var severity = (double)reader.GetDecimal(2);
        var description = reader.IsDBNull(3) ? null : reader.GetString(3);
        var detectedAt = DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc);
        return new LatestAnomalyDto(
            AnomalyType: anomalyType,
            ReadingTime: readingTime,
            Severity: severity,
            Description: description,
            DetectedAt: detectedAt);
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
