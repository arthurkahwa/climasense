// SPDX-License-Identifier: MIT
//
// AlertScanService — slice-11 threshold engine core.
//
// Per ADR-0007 + epic #2 "Alerts and SSE":
//
//   * Per-rule gaps-and-islands SQL over `dbo.SensorReadings` for the
//     window `[asOf - 24h, asOf]`.
//   * Closure-only delivery: a breach interval whose last in-breach
//     reading equals @asOf does NOT fire (waits one tick).
//   * Idempotent re-detection via `INSERT … WHERE NOT EXISTS` against
//     the `UNIQUE(RuleId, BreachStart)` constraint on `dbo.Alerts`.
//   * The metric/operator are whitelisted at the POCO layer
//     (`AlertRule.MetricColumn` / `AlertRule.SqlOperator`) so the
//     templated predicate carries no caller-controlled SQL strings.
//
// Interface emergence policy (ADR-0011):
//   This class is concrete; the two SQL operations are parameterised
//   as delegates so tests can swap a lambda. No speculative
//   `IAlertScanService` interface — when a second scan strategy
//   arrives, the interface is extracted from the concrete shape(s).

#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClimaSense.Web.Cursor;

namespace ClimaSense.Web.Alerts;

/// <summary>
/// One closed breach interval discovered by the scanner for a single
/// rule. <c>BreachEnd</c> is strictly less than <c>asOf</c> — that's
/// the closure-only rule from ADR-0007.
/// </summary>
public sealed record BreachInterval(
    int RuleId,
    DateTime BreachStart,
    DateTime BreachEnd,
    double PeakValue);

/// <summary>
/// Delegate seam — runs the per-rule gaps-and-islands SQL against
/// `dbo.SensorReadings` in <c>[asOf - 24h, asOf]</c> and returns the
/// set of closed breach intervals at least <c>DurationMinutes</c>
/// long.
/// </summary>
public delegate Task<IReadOnlyList<BreachInterval>> RuleBreachScanner(
    AlertRule rule,
    DateTime asOf,
    CancellationToken cancellationToken);

/// <summary>
/// Delegate seam — insert one breach row into `dbo.Alerts` if the
/// <c>UNIQUE(RuleId, BreachStart)</c> slot is empty. Returns the
/// inserted <c>AlertId</c> when a row landed, or <c>null</c> when the
/// slot was already taken (idempotent silent no-op).
/// </summary>
public delegate Task<long?> AlertInserter(
    BreachInterval breach,
    DateTime replayClockAtFire,
    CancellationToken cancellationToken);

/// <summary>
/// Delegate seam — load all enabled alert rules. Re-read each tick so
/// an operator toggling `Enabled` (or adding a rule via the deferred
/// CRUD UI) takes effect immediately.
/// </summary>
public delegate Task<IReadOnlyList<AlertRule>> AlertRulesLoader(
    CancellationToken cancellationToken);

public sealed class AlertScanService
{
    /// <summary>
    /// Replay-time lookback. Per ADR-0007 + the brief: every tick scans
    /// the last 24 h of <c>dbo.SensorReadings</c>.
    /// </summary>
    public static readonly TimeSpan LookbackWindow = TimeSpan.FromHours(24);

    private readonly AlertRulesLoader _rulesLoader;
    private readonly RuleBreachScanner _breachScanner;
    private readonly AlertInserter _alertInserter;

    public AlertScanService(
        AlertRulesLoader rulesLoader,
        RuleBreachScanner breachScanner,
        AlertInserter alertInserter)
    {
        ArgumentNullException.ThrowIfNull(rulesLoader);
        ArgumentNullException.ThrowIfNull(breachScanner);
        ArgumentNullException.ThrowIfNull(alertInserter);
        _rulesLoader = rulesLoader;
        _breachScanner = breachScanner;
        _alertInserter = alertInserter;
    }

    /// <summary>
    /// One scan tick.
    ///
    /// For each enabled rule, runs the gaps-and-islands query, INSERTs
    /// any newly-closed breach interval not already in
    /// <c>dbo.Alerts</c>, and returns the set of rows that actually
    /// landed (so the caller can emit one SSE
    /// <c>event: breach-detected</c> per row).
    /// </summary>
    public async Task<IReadOnlyList<NewAlert>> ScanOnceAsync(
        CursorSnapshot cursor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cursor);

        var rules = await _rulesLoader(cancellationToken).ConfigureAwait(false);
        var emitted = new List<NewAlert>();

        // Capture the cursor once at the top of the tick — every rule
        // scan, every insert, and every `ReplayClockAtFire` stamp uses
        // the same value (the snapshot semantic from ADR-0011).
        var asOf = cursor.AsOf;

        foreach (var rule in rules)
        {
            if (!rule.Enabled)
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();

            var intervals = await _breachScanner(rule, asOf, cancellationToken)
                .ConfigureAwait(false);

            foreach (var interval in intervals)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var alertId = await _alertInserter(interval, asOf, cancellationToken)
                    .ConfigureAwait(false);
                if (alertId is { } id)
                {
                    emitted.Add(new NewAlert(
                        AlertId: id,
                        Rule: rule,
                        Breach: interval,
                        ReplayClockAtFire: asOf));
                }
                // alertId == null means UNIQUE(RuleId, BreachStart)
                // already held a row — silent no-op (idempotency).
            }
        }

        return emitted;
    }

    /// <summary>
    /// Render the per-rule gaps-and-islands SQL.
    ///
    /// Pinned by golden-string tests so the closure-only filter
    /// (<c>MAX(ReadingTime) &lt; @asOf</c>), the duration filter
    /// (<c>DATEDIFF(MINUTE, MIN, MAX) &gt;= @durationMinutes</c>), and
    /// the run-segmentation rule (gap &gt; 5 minutes splits the run)
    /// can't silently change shape.
    ///
    /// The metric column (<c>Temperature</c> / <c>Humidity</c>),
    /// comparison operator (<c>&gt;</c> / <c>&lt;</c>), and peak
    /// aggregate (<c>MAX</c> / <c>MIN</c>) are whitelisted at the
    /// <see cref="AlertRule"/> POCO layer so the rendered SQL carries
    /// no caller-controlled strings. The numeric threshold + duration
    /// + window endpoints + cursor are all bound as parameters.
    /// </summary>
    public static string RenderGapsAndIslandsSql(AlertRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var col = rule.MetricColumn;
        var op = rule.SqlOperator;
        var peak = rule.PeakAggregate;

        return $"""
        WITH flagged AS (
            SELECT ReadingTime,
                   {col} AS Metric,
                   CASE WHEN {col} {op} @threshold THEN 1 ELSE 0 END AS InBreach,
                   LAG(ReadingTime) OVER (ORDER BY ReadingTime) AS PrevTime
              FROM dbo.SensorReadings
             WHERE ReadingTime >= @windowStart
               AND ReadingTime <= @asOf
        ),
        groups AS (
            SELECT ReadingTime,
                   Metric,
                   InBreach,
                   SUM(CASE
                         WHEN InBreach = 0
                           OR PrevTime IS NULL
                           OR DATEDIFF(MINUTE, PrevTime, ReadingTime) > 5
                         THEN 1
                         ELSE 0
                       END)
                     OVER (ORDER BY ReadingTime ROWS UNBOUNDED PRECEDING) AS GroupId
              FROM flagged
        )
        SELECT MIN(ReadingTime) AS BreachStart,
               MAX(ReadingTime) AS BreachEnd,
               {peak}(Metric)   AS PeakValue
          FROM groups
         WHERE InBreach = 1
         GROUP BY GroupId
        HAVING DATEDIFF(MINUTE, MIN(ReadingTime), MAX(ReadingTime)) >= @durationMinutes
           AND MAX(ReadingTime) < @asOf
         ORDER BY MIN(ReadingTime) ASC;
        """;
    }
}

/// <summary>
/// One row that the scanner just landed in <c>dbo.Alerts</c>. Carries
/// enough context for the SSE emission and for tests that pin payload
/// shape.
/// </summary>
public sealed record NewAlert(
    long AlertId,
    AlertRule Rule,
    BreachInterval Breach,
    DateTime ReplayClockAtFire);
