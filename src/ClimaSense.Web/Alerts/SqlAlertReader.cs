// SPDX-License-Identifier: MIT
//
// SqlAlertReader — production adapter for `AlertHistoryFetcher`.
//
// Reads through the inline TVF `dbo.fv_alerts_at_cursor(@asOf)`
// defined in `init-db.sql §3.5` so cursor-clipping is enforced by the
// schema, not by caller discipline. Joins against `dbo.AlertRules` so
// the wire row carries the human-readable rule name and the
// `Metric/Operator/Threshold/DurationMinutes` summary directly — the
// dashboard never has to second-hop to render the history table.

#nullable enable

using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ClimaSense.Web.Alerts;

public sealed class SqlAlertReader
{
    private readonly string _connectionString;
    private readonly ILogger<SqlAlertReader> _logger;

    public SqlAlertReader(
        IConfiguration configuration,
        ILogger<SqlAlertReader> logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);
        _connectionString = BuildConnectionString(configuration);
        _logger = logger;
    }

    /// <summary>
    /// Golden-pinned SQL: most-recent N alerts at the cursor, joined
    /// against <c>dbo.AlertRules</c> for the human-readable label and
    /// summary fields. Cursor-clipping goes through the TVF
    /// (<c>WHERE ReplayClockAtFire &lt;= @asOf</c>) — the inner join
    /// + <c>TOP (@limit)</c> sit on top.
    /// </summary>
    public const string HistorySql = """
        SELECT TOP (@limit)
               a.AlertId,
               a.RuleId,
               r.Name,
               r.Metric,
               r.Operator,
               r.Threshold,
               r.DurationMinutes,
               a.BreachStart,
               a.BreachEnd,
               a.PeakValue,
               a.ReplayClockAtFire
          FROM dbo.fv_alerts_at_cursor(@asOf) AS a
          INNER JOIN dbo.AlertRules AS r ON r.RuleId = a.RuleId
         ORDER BY a.ReplayClockAtFire DESC, a.AlertId DESC;
        """;

    public async Task<IReadOnlyList<AlertRowDto>> FetchHistoryAsync(
        DateTime asOf,
        int limit,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = HistorySql;
            command.CommandType = CommandType.Text;
            AddDateTime2(command, "@asOf", asOf);
            AddInt(command, "@limit", limit);

            await using var reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            var rows = new List<AlertRowDto>();
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
            _logger.LogWarning(ex, "AlertReadService: history SQL read failed");
            throw;
        }
    }

    private static AlertRowDto ReadRow(SqlDataReader reader)
    {
        var alertId = reader.GetInt64(0);
        var ruleId = reader.GetInt32(1);
        var name = reader.GetString(2);
        var metric = reader.GetString(3);
        var op = reader.GetString(4);
        var threshold = (double)reader.GetDecimal(5);
        var durationMinutes = reader.GetInt32(6);
        var breachStart = DateTime.SpecifyKind(reader.GetDateTime(7), DateTimeKind.Utc);
        var breachEnd = DateTime.SpecifyKind(reader.GetDateTime(8), DateTimeKind.Utc);
        var peakValue = (double)reader.GetDecimal(9);
        var replayClockAtFire = DateTime.SpecifyKind(reader.GetDateTime(10), DateTimeKind.Utc);

        // Build the summary on the .NET side so it stays in lockstep
        // with `AlertRule.Summary` (one source of truth for the wire
        // format). This avoids smuggling SQL string-format logic into
        // a query.
        var rule = new AlertRule(
            RuleId: ruleId,
            Name: name,
            Metric: metric,
            Operator: op,
            Threshold: threshold,
            DurationMinutes: durationMinutes,
            Enabled: true);

        return new AlertRowDto(
            AlertId: alertId,
            RuleId: ruleId,
            RuleName: name,
            RuleSummary: rule.Summary,
            BreachStart: breachStart,
            BreachEnd: breachEnd,
            PeakValue: peakValue,
            ReplayClockAtFire: replayClockAtFire);
    }

    private static void AddDateTime2(SqlCommand command, string name, DateTime value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.SqlDbType = SqlDbType.DateTime2;
        p.Value = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
        command.Parameters.Add(p);
    }

    private static void AddInt(SqlCommand command, string name, int value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.SqlDbType = SqlDbType.Int;
        p.Value = value;
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
