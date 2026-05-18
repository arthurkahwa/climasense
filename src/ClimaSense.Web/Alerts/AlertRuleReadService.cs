// SPDX-License-Identifier: MIT
//
// AlertRuleReadService — slice-11 read facade for `dbo.AlertRules`.
//
// Lists the enabled rules so the dashboard can render a "rules in
// force" inventory and so the toast handler knows the human-readable
// summary for a `breach-detected` event.
//
// CRUD UI is deferred (per ADR-0011 + epic #2); this is a read-only
// surface.

#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ClimaSense.Web.Alerts;

/// <summary>
/// Delegate used by <see cref="AlertRuleReadService"/> to enumerate
/// every enabled rule in <c>dbo.AlertRules</c>.
/// </summary>
public delegate Task<IReadOnlyList<AlertRule>> EnabledRulesFetcher(
    CancellationToken cancellationToken);

public sealed class AlertRuleReadService
{
    private readonly EnabledRulesFetcher _rulesFetcher;

    public AlertRuleReadService(EnabledRulesFetcher rulesFetcher)
    {
        ArgumentNullException.ThrowIfNull(rulesFetcher);
        _rulesFetcher = rulesFetcher;
    }

    public async Task<AlertRulesResponseDto> GetAllAsync(
        CancellationToken cancellationToken)
    {
        var rules = await _rulesFetcher(cancellationToken).ConfigureAwait(false);
        var rows = new List<AlertRuleRowDto>(rules.Count);
        foreach (var r in rules)
        {
            rows.Add(new AlertRuleRowDto(
                RuleId: r.RuleId,
                Name: r.Name,
                Metric: r.Metric,
                Operator: r.Operator,
                Threshold: r.Threshold,
                DurationMinutes: r.DurationMinutes,
                Enabled: r.Enabled,
                Summary: r.Summary));
        }
        return new AlertRulesResponseDto(Rules: rows);
    }
}
