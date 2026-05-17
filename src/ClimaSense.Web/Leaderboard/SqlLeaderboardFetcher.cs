// SPDX-License-Identifier: MIT
//
// SqlLeaderboardFetcher — production adapter for `LeaderboardFetcher`.
//
// Reads `dbo.Leaderboard` (slice 6) ordered by `Mae ASC` so the
// best-performing model surfaces first on the Analysis page. The
// per-column projection mirrors the contract's `LeaderboardRow` shape
// exactly — `Mape` and `Smape` are nullable on the wire because the
// notebook's sequence_results block doesn't report them.
//
// The schema (`init-db.sql §2.8`) pins:
//
//   CREATE TABLE dbo.Leaderboard (
//       LeaderboardId BIGINT IDENTITY(1,1) NOT NULL,
//       ModelName     VARCHAR(64) NOT NULL,
//       Mae           DECIMAL(8, 4) NOT NULL,
//       Rmse          DECIMAL(8, 4) NOT NULL,
//       Mape          DECIMAL(8, 4) NULL,
//       Smape         DECIMAL(8, 4) NULL,
//       Provenance    VARCHAR(16) NOT NULL,
//       EvaluatedAt   DATETIME2(3) NOT NULL ...,
//       CONSTRAINT UQ_Leaderboard_Model UNIQUE (ModelName),
//       CONSTRAINT CK_Leaderboard_Provenance CHECK
//           (Provenance IN (N'notebook', N'live'))
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

namespace ClimaSense.Web.Leaderboard;

public sealed class SqlLeaderboardFetcher
{
    private readonly string _connectionString;
    private readonly ILogger<SqlLeaderboardFetcher> _logger;

    public SqlLeaderboardFetcher(
        IConfiguration configuration,
        ILogger<SqlLeaderboardFetcher> logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);
        _connectionString = BuildConnectionString(configuration);
        _logger = logger;
    }

    /// <summary>
    /// SQL pinned by golden-string tests so the column projection /
    /// order can't silently change shape. Ordered by `Mae` ascending
    /// — the most accurate model surfaces first on the Analysis page.
    /// </summary>
    public const string Sql = """
        SELECT ModelName, Mae, Rmse, Mape, Smape, Provenance, EvaluatedAt
          FROM dbo.Leaderboard
         ORDER BY Mae ASC, ModelName ASC;
        """;

    public async Task<IReadOnlyList<LeaderboardRowDto>> FetchAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = Sql;
            command.CommandType = CommandType.Text;

            await using var reader = await command
                .ExecuteReaderAsync(CommandBehavior.Default, cancellationToken)
                .ConfigureAwait(false);

            var rows = new List<LeaderboardRowDto>(16);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var modelName = reader.GetString(0);
                var mae = (double)reader.GetDecimal(1);
                var rmse = (double)reader.GetDecimal(2);
                double? mape = reader.IsDBNull(3) ? null : (double)reader.GetDecimal(3);
                double? smape = reader.IsDBNull(4) ? null : (double)reader.GetDecimal(4);
                var provenance = reader.GetString(5);
                var evaluatedAt = DateTime.SpecifyKind(
                    reader.GetDateTime(6), DateTimeKind.Utc);
                rows.Add(new LeaderboardRowDto(
                    ModelName: modelName,
                    Mae: mae,
                    Rmse: rmse,
                    Mape: mape,
                    Smape: smape,
                    Provenance: provenance,
                    EvaluatedAt: evaluatedAt));
            }

            return rows;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SqlException ex)
        {
            _logger.LogWarning(ex, "LeaderboardReadService: SQL read failed");
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
