// SPDX-License-Identifier: MIT
//
// SqlProfileFetcher — production adapter for `DayProfileRangeFetcher`.
//
// Reads through the inline TVF
// `dbo.fv_dayprofiles_at_cursor(@asOf)` defined in `init-db.sql §3.3`
// so cursor-clipping is enforced by the schema, not by caller
// discipline. The TVF projects `dbo.DayProfiles` with
// `WHERE ComputedAt <= @asOf`.

#nullable enable

using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ClimaSense.Web.Profiles;

public sealed class SqlProfileFetcher
{
    private readonly string _connectionString;
    private readonly ILogger<SqlProfileFetcher> _logger;

    public SqlProfileFetcher(
        IConfiguration configuration,
        ILogger<SqlProfileFetcher> logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);
        _connectionString = BuildConnectionString(configuration);
        _logger = logger;
    }

    /// <summary>
    /// SQL pinned by golden-string tests so cursor-clipping via the
    /// TVF can't silently change shape. Returns rows in
    /// <c>[start, end]</c> ordered by <c>Date</c> ascending.
    /// </summary>
    public const string RangeSql = """
        SELECT [Date], DayOfWeek, MeanResidual, MaxAbsZscore, Pattern, ComputedAt
          FROM dbo.fv_dayprofiles_at_cursor(@asOf)
         WHERE [Date] >= @startDate
           AND [Date] <= @endDate
         ORDER BY [Date] ASC;
        """;

    public async Task<IReadOnlyList<DayProfileDto>> FetchRangeAsync(
        DateTime asOf,
        DateOnly start,
        DateOnly end,
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
            AddDate(command, "@startDate", start);
            AddDate(command, "@endDate", end);

            await using var reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            var rows = new List<DayProfileDto>();
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
            _logger.LogWarning(ex, "ProfileReadService: range SQL read failed");
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

    private static void AddDate(SqlCommand command, string name, DateOnly value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.SqlDbType = SqlDbType.Date;
        p.Value = value.ToDateTime(TimeOnly.MinValue);
        command.Parameters.Add(p);
    }

    private static DayProfileDto ReadRow(SqlDataReader reader)
    {
        // Schema (init-db.sql §2.4):
        //   [Date]        DATE NOT NULL
        //   DayOfWeek     TINYINT NOT NULL
        //   MeanResidual  DECIMAL(8, 4) NOT NULL
        //   MaxAbsZscore  DECIMAL(8, 4) NOT NULL
        //   Pattern       VARCHAR(16) NOT NULL
        //   ComputedAt    DATETIME2(3) NOT NULL DEFAULT (SYSUTCDATETIME())
        var date = DateOnly.FromDateTime(reader.GetDateTime(0));
        var dayOfWeek = reader.GetByte(1);
        var meanResidual = (double)reader.GetDecimal(2);
        var maxAbsZscore = (double)reader.GetDecimal(3);
        var pattern = reader.GetString(4);
        var computedAt = DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc);
        return new DayProfileDto(
            Date: date,
            DayOfWeek: dayOfWeek,
            MeanResidual: meanResidual,
            MaxAbsZscore: maxAbsZscore,
            Pattern: pattern,
            ComputedAt: computedAt);
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
