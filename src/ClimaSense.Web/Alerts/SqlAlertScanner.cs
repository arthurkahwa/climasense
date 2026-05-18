// SPDX-License-Identifier: MIT
//
// SqlAlertScanner — production SQL adapter for the slice-11 threshold
// engine.
//
// Three responsibilities, each one wired into `AlertScanService` via a
// delegate seam:
//
//   1. `LoadRulesAsync`  → enumerate all enabled rules from
//                          `dbo.AlertRules` (re-read per tick).
//   2. `ScanBreachesAsync` → run the per-rule gaps-and-islands query
//                          (built by `AlertScanService.RenderGapsAndIslandsSql`)
//                          over `dbo.SensorReadings` in
//                          `[asOf - 24h, asOf]`.
//   3. `InsertAlertAsync` → idempotent `INSERT … WHERE NOT EXISTS`
//                          against `UNIQUE(RuleId, BreachStart)` on
//                          `dbo.Alerts`, returning the new `AlertId`
//                          or `null` when the slot was taken.
//
// The SQL strings are public so the unit tests can pin them
// (golden-string lock) — same convention as the slice-7/8/9/10
// fetchers.

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

public sealed class SqlAlertScanner
{
    private readonly string _connectionString;
    private readonly ILogger<SqlAlertScanner> _logger;

    public SqlAlertScanner(
        IConfiguration configuration,
        ILogger<SqlAlertScanner> logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);
        _connectionString = BuildConnectionString(configuration);
        _logger = logger;
    }

    // -----------------------------------------------------------------
    // (1) Rules loader.
    // -----------------------------------------------------------------

    /// <summary>
    /// Golden-pinned SQL: enumerate every enabled rule in
    /// <c>dbo.AlertRules</c> ordered by <c>RuleId</c>.
    /// </summary>
    public const string LoadRulesSql = """
        SELECT RuleId, Name, Metric, Operator, Threshold, DurationMinutes, Enabled
          FROM dbo.AlertRules
         WHERE Enabled = 1
         ORDER BY RuleId ASC;
        """;

    public async Task<IReadOnlyList<AlertRule>> LoadRulesAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = LoadRulesSql;
        command.CommandType = CommandType.Text;

        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        var rules = new List<AlertRule>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rules.Add(new AlertRule(
                RuleId: reader.GetInt32(0),
                Name: reader.GetString(1),
                Metric: reader.GetString(2),
                Operator: reader.GetString(3),
                Threshold: (double)reader.GetDecimal(4),
                DurationMinutes: reader.GetInt32(5),
                Enabled: reader.GetBoolean(6)));
        }
        return rules;
    }

    // -----------------------------------------------------------------
    // (2) Per-rule breach scanner.
    // -----------------------------------------------------------------

    public async Task<IReadOnlyList<BreachInterval>> ScanBreachesAsync(
        AlertRule rule,
        DateTime asOf,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rule);

        var sql = AlertScanService.RenderGapsAndIslandsSql(rule);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandType = CommandType.Text;
        AddDateTime2(command, "@windowStart", asOf - AlertScanService.LookbackWindow);
        AddDateTime2(command, "@asOf", asOf);
        AddDecimal(command, "@threshold", (decimal)rule.Threshold);
        AddInt(command, "@durationMinutes", rule.DurationMinutes);

        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        var intervals = new List<BreachInterval>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var start = DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc);
            var end = DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc);
            var peak = (double)reader.GetDecimal(2);
            intervals.Add(new BreachInterval(
                RuleId: rule.RuleId,
                BreachStart: start,
                BreachEnd: end,
                PeakValue: peak));
        }
        return intervals;
    }

    // -----------------------------------------------------------------
    // (3) Idempotent inserter. Returns the new AlertId, or null when
    //     the slot was already taken.
    // -----------------------------------------------------------------

    /// <summary>
    /// Golden-pinned SQL: insert one breach row iff
    /// <c>UNIQUE(RuleId, BreachStart)</c> is empty for that pair.
    /// Returns <c>SCOPE_IDENTITY()</c> (the new <c>AlertId</c>) or
    /// <c>NULL</c> via OUTPUT to signal the silent no-op path.
    /// </summary>
    /// <remarks>
    /// We use <c>INSERT … SELECT … WHERE NOT EXISTS</c> rather than
    /// trapping the duplicate-key exception so the production code
    /// path never throws on a re-detection. <c>OUTPUT INSERTED.AlertId
    /// INTO @out</c> + a final <c>SELECT</c> from <c>@out</c>
    /// disambiguates "row inserted" (one row in output) vs
    /// "row already present" (zero rows in output).
    /// </remarks>
    public const string InsertAlertSql = """
        DECLARE @out TABLE (AlertId BIGINT);
        INSERT INTO dbo.Alerts
            (RuleId, BreachStart, BreachEnd, PeakValue, ReplayClockAtFire)
        OUTPUT INSERTED.AlertId INTO @out (AlertId)
        SELECT @ruleId, @breachStart, @breachEnd, @peakValue, @replayClockAtFire
         WHERE NOT EXISTS (
             SELECT 1 FROM dbo.Alerts WITH (UPDLOCK, HOLDLOCK)
              WHERE RuleId = @ruleId
                AND BreachStart = @breachStart
         );
        SELECT AlertId FROM @out;
        """;

    public async Task<long?> InsertAlertAsync(
        BreachInterval breach,
        DateTime replayClockAtFire,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(breach);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = InsertAlertSql;
        command.CommandType = CommandType.Text;
        AddInt(command, "@ruleId", breach.RuleId);
        AddDateTime2(command, "@breachStart", breach.BreachStart);
        AddDateTime2(command, "@breachEnd", breach.BreachEnd);
        AddDecimal(command, "@peakValue", (decimal)breach.PeakValue);
        AddDateTime2(command, "@replayClockAtFire", replayClockAtFire);

        var scalar = await command
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        if (scalar is null || scalar is DBNull)
        {
            return null;
        }
        return Convert.ToInt64(scalar);
    }

    // -----------------------------------------------------------------

    private static void AddDateTime2(SqlCommand command, string name, DateTime value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.SqlDbType = SqlDbType.DateTime2;
        p.Value = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
        command.Parameters.Add(p);
    }

    private static void AddDecimal(SqlCommand command, string name, decimal value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.SqlDbType = SqlDbType.Decimal;
        p.Precision = 7;
        p.Scale = 3;
        p.Value = value;
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
            // Per-rule scan can take longer than the 5-second default
            // when the data has wide breach intervals (the window
            // functions read 24 h of rows = up to ~14,400 rows at
            // 1-minute cadence). 30 s is the upper bound observed
            // empirically; raised here so the tick doesn't surface
            // spurious timeouts.
            CommandTimeout = 30,
        };
        return b.ConnectionString;
    }
}
