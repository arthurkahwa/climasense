// SPDX-License-Identifier: MIT
//
// SqlForecastFetcher — production adapter for the
// `LatestForecastBatchFetcher` delegate.
//
// Reads via the inline TVF `dbo.fv_forecasts_at_cursor(@asOf)` so
// cursor-clipping is enforced by the schema. Returns the rows of the
// most recent batch (largest `GeneratedAt` ≤ `@asOf`) ordered by
// `TargetTime ASC`.
//
// The TVF is defined in `scripts/init-db.sql §3.1` and pins:
//
//   CREATE FUNCTION dbo.fv_forecasts_at_cursor (@asOf DATETIME2(3))
//   RETURNS TABLE AS RETURN (
//       SELECT ForecastId, GeneratedAt, TargetTime,
//              PredictedTemperature, PredictedHumidity,
//              ConfidenceLowerTemp, ConfidenceUpperTemp, ModelVersion
//         FROM dbo.Forecasts
//        WHERE GeneratedAt <= @asOf
//   );

#nullable enable

using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ClimaSense.Web.Forecasts;

public sealed class SqlForecastFetcher
{
    private readonly string _connectionString;
    private readonly ILogger<SqlForecastFetcher> _logger;

    public SqlForecastFetcher(
        IConfiguration configuration,
        ILogger<SqlForecastFetcher> logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);
        _connectionString = BuildConnectionString(configuration);
        _logger = logger;
    }

    /// <summary>
    /// SQL pinned by golden-string tests so cursor-clipping via the
    /// TVF can't silently change shape. Two statements:
    /// (a) resolve `@latest = MAX(GeneratedAt)` from the TVF;
    /// (b) project the matching rows.
    /// </summary>
    public const string Sql = """
        DECLARE @latest DATETIME2(3) =
            (SELECT MAX(GeneratedAt) FROM dbo.fv_forecasts_at_cursor(@asOf));
        SELECT GeneratedAt, TargetTime,
               PredictedTemperature, PredictedHumidity,
               ConfidenceLowerTemp, ConfidenceUpperTemp, ModelVersion
          FROM dbo.fv_forecasts_at_cursor(@asOf)
         WHERE GeneratedAt = @latest
         ORDER BY TargetTime ASC;
        """;

    public async Task<(DateTime? GeneratedAt, string? ModelVersion, IReadOnlyList<ForecastPointDto> Points)>
        FetchAsync(DateTime asOf, CancellationToken cancellationToken)
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
                .ExecuteReaderAsync(CommandBehavior.Default, cancellationToken)
                .ConfigureAwait(false);

            DateTime? generatedAt = null;
            string? modelVersion = null;
            var points = new List<ForecastPointDto>(72);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                generatedAt ??= DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc);
                var targetTime = DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc);
                var predT = (double)reader.GetDecimal(2);
                var predH = (double)reader.GetDecimal(3);
                var ciLow = reader.IsDBNull(4) ? predT : (double)reader.GetDecimal(4);
                var ciHigh = reader.IsDBNull(5) ? predT : (double)reader.GetDecimal(5);
                modelVersion ??= reader.GetString(6);
                points.Add(new ForecastPointDto(targetTime, predT, predH, ciLow, ciHigh));
            }

            return (generatedAt, modelVersion, points);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SqlException ex)
        {
            _logger.LogWarning(ex, "ForecastReadService: SQL read failed");
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
