// SPDX-License-Identifier: MIT
//
// SqlComfortFetcher — production adapter for `CurrentComfortFetcher`.
//
// Reads via the inline TVF `dbo.fv_comfortscores_at_cursor(@asOf)` so
// cursor-clipping is enforced by the schema. Returns the row with the
// largest `BucketTime` that is ≤ `@asOf`, or `null` when no comfort
// row is visible at the cursor yet (typically only seen during the
// brief window before the comfort scheduler emits its first row).
//
// The TVF is defined in `scripts/init-db.sql §3.4` and pins:
//
//   CREATE FUNCTION dbo.fv_comfortscores_at_cursor (@asOf DATETIME2(3))
//   RETURNS TABLE AS RETURN (
//       SELECT ComfortScoreId, BucketTime, Score, Rating, Season, ComputedAt
//         FROM dbo.ComfortScores
//        WHERE BucketTime <= @asOf
//   );

#nullable enable

using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ClimaSense.Web.Comfort;

public sealed class SqlComfortFetcher
{
    private readonly string _connectionString;
    private readonly ILogger<SqlComfortFetcher> _logger;

    public SqlComfortFetcher(
        IConfiguration configuration,
        ILogger<SqlComfortFetcher> logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);
        _connectionString = BuildConnectionString(configuration);
        _logger = logger;
    }

    /// <summary>
    /// SQL pinned by golden-string tests so cursor-clipping via the
    /// TVF can't silently change shape. Returns at most one row:
    /// the latest comfort score visible at the cursor.
    /// </summary>
    public const string Sql = """
        SELECT TOP 1 BucketTime, Score, Rating, Season, ComputedAt
          FROM dbo.fv_comfortscores_at_cursor(@asOf)
         ORDER BY BucketTime DESC;
        """;

    public async Task<CurrentComfortDto?> FetchAsync(
        DateTime asOf,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = Sql;
            command.CommandType = CommandType.Text;
            var asOfParam = command.CreateParameter();
            asOfParam.ParameterName = "@asOf";
            asOfParam.SqlDbType = SqlDbType.DateTime2;
            asOfParam.Value = DateTime.SpecifyKind(asOf, DateTimeKind.Unspecified);
            command.Parameters.Add(asOfParam);

            await using var reader = await command
                .ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken)
                .ConfigureAwait(false);

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            // Schema (init-db.sql §2.5):
            //   BucketTime DATETIME2(3) NOT NULL
            //   Score      DECIMAL(5, 2) NOT NULL
            //   Rating     VARCHAR(16) NOT NULL  CHECK IN (...)
            //   Season     VARCHAR(8)  NOT NULL  CHECK IN ('summer', 'winter')
            //   ComputedAt DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME()
            var bucketTime = DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc);
            var score = (double)reader.GetDecimal(1);
            var rating = reader.GetString(2);
            var season = reader.GetString(3);
            var computedAt = DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc);
            return new CurrentComfortDto(
                Score: score,
                Rating: rating,
                Season: season,
                BucketTime: bucketTime,
                ComputedAt: computedAt);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SqlException ex)
        {
            _logger.LogWarning(ex, "ComfortReadService: SQL read failed");
            throw;
        }
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
