// SPDX-License-Identifier: MIT
//
// SqlLatestReadingFetcher — production adapter for
// `LatestReadingFetcher` (see SensorDataService.cs).
//
// Owns:
//   * Connection-string construction from `IConfiguration` (matches
//     Program.cs's `BuildConnectionString` helper).
//   * The cursor-clipped SELECT TOP 1 query.
//   * Reading the row into a `LatestReading`, coercing the SQL
//     `DECIMAL(6, 3)` columns to `double` for the wire shape.
//
// The fetcher is registered as a transient service in Program.cs and
// wired into `SensorDataService` via a delegate cast — that is the
// "single seam" `SensorDataService` cares about.

#nullable enable

using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ClimaSense.Web.Readings;

public sealed class SqlLatestReadingFetcher
{
    private readonly string _connectionString;
    private readonly ILogger<SqlLatestReadingFetcher> _logger;

    public SqlLatestReadingFetcher(
        IConfiguration configuration,
        ILogger<SqlLatestReadingFetcher> logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);
        _connectionString = BuildConnectionString(configuration);
        _logger = logger;
    }

    /// <summary>
    /// Execute the cursor-clipped SELECT TOP 1 and return the row, or
    /// <c>null</c> when the table is empty.
    /// </summary>
    public async Task<LatestReading?> FetchAsync(
        DateTime asOf,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP 1 ReadingTime, Temperature, Humidity
              FROM dbo.SensorReadings
             WHERE ReadingTime <= @asOf
             ORDER BY ReadingTime DESC;
            """;

        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = sql;
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

            // Schema (init-db.sql §2.1):
            //   ReadingTime DATETIME2(3) NOT NULL
            //   Temperature DECIMAL(6, 3) NOT NULL
            //   Humidity    DECIMAL(6, 3) NOT NULL
            var readingTime = DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc);
            var temperature = (double)reader.GetDecimal(1);
            var humidity = (double)reader.GetDecimal(2);
            return new LatestReading(readingTime, temperature, humidity);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SqlException ex)
        {
            _logger.LogWarning(ex, "SensorDataService: SQL read failed");
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
