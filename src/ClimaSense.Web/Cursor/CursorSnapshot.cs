using System.Text;
using ClimaSense.Web.Clock;

namespace ClimaSense.Web.Cursor;

/// <summary>
/// Immutable snapshot of the replay cursor at the entry of a logical
/// operation. Captures the rule "read <c>clock.UtcNow()</c> once per
/// operation."
/// </summary>
/// <remarks>
/// <para>
/// Lifetime: registered as a <em>scoped</em> DI service. Within an
/// ASP.NET Core HTTP request scope, the same instance is resolved
/// every time — guaranteeing one cursor value per request. A long-running
/// background operation that needs a fresh cursor must explicitly
/// construct a new snapshot via <see cref="FromClock"/>.
/// </para>
/// <para>
/// Mirrored hand-written implementation in Python at
/// <c>src/ClimaSense.ML/climasense_ml/cursor.py</c>. Parity is enforced
/// by code review against CONTEXT.md.
/// </para>
/// </remarks>
public sealed class CursorSnapshot
{
    /// <summary>Construct directly from an instant. Prefer <see cref="FromClock"/>.</summary>
    public CursorSnapshot(DateTime asOf)
    {
        if (asOf.Kind == DateTimeKind.Local)
        {
            asOf = asOf.ToUniversalTime();
        }
        else if (asOf.Kind == DateTimeKind.Unspecified)
        {
            asOf = DateTime.SpecifyKind(asOf, DateTimeKind.Utc);
        }

        AsOf = asOf;
    }

    /// <summary>The frozen cursor value, always UTC.</summary>
    public DateTime AsOf { get; }

    /// <summary>Build a snapshot from the registered clock. Called once per scope.</summary>
    public static CursorSnapshot FromClock(IClock clock) => new(clock.UtcNow());

    /// <summary>
    /// Append a cursor-clipping <c>WHERE ReadingTime &lt;= @asOf</c> clause
    /// to a SQL string targeting the raw <c>SensorReadings</c> table. Returns
    /// the new query plus the parameter value to bind as <c>@asOf</c>.
    /// </summary>
    /// <param name="query">Existing SQL query against <c>SensorReadings</c>.</param>
    /// <param name="asOfParameterName">
    /// Parameter name used in the appended clause. Defaults to <c>@asOf</c>;
    /// callers using a different name (e.g. <c>@as_of_time</c>) may override.
    /// </param>
    /// <returns>
    /// Tuple of <c>(modifiedQuery, parameterValue)</c>. The caller binds
    /// <paramref name="asOfParameterName"/> = <see cref="AsOf"/> on the command.
    /// </returns>
    /// <remarks>
    /// Per CONTEXT.md, this method clips <em>raw</em> <c>SensorReadings</c>
    /// only. Derived tables (Forecasts, Anomalies, DayProfiles, ComfortScores,
    /// Alerts) clip via the inline TVFs <c>dbo.fv_&lt;table&gt;_at_cursor</c>
    /// in <c>scripts/init-db.sql</c>.
    /// </remarks>
    public (string Query, DateTime AsOfParameterValue) Clip(
        string query,
        string asOfParameterName = "@asOf")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(asOfParameterName);

        var trimmed = query.TrimEnd();
        var hasWhere = trimmed.Contains(" WHERE ", StringComparison.OrdinalIgnoreCase);
        var separator = hasWhere ? " AND " : " WHERE ";
        var sb = new StringBuilder(trimmed)
            .Append(separator)
            .Append("ReadingTime <= ")
            .Append(asOfParameterName);
        return (sb.ToString(), AsOf);
    }

    /// <summary>
    /// Produce a scan window <c>(start, end)</c> = <c>(AsOf - duration, AsOf)</c>.
    /// Used by the alert engine and the anomaly detectors.
    /// </summary>
    public (DateTime Start, DateTime End) Windowed(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(duration),
                "Window duration must be positive.");
        }

        return (AsOf - duration, AsOf);
    }

    /// <summary>
    /// β-prime emission gate. Returns <c>true</c> iff the elapsed replay-time
    /// since <paramref name="lastEmit"/> meets or exceeds <paramref name="cadence"/>.
    /// </summary>
    /// <param name="lastEmit">
    /// Last emission instant (UTC). <c>null</c> means "never emitted yet" —
    /// the gate opens unconditionally.
    /// </param>
    /// <param name="cadence">Required replay-time gap between emissions.</param>
    public bool ShouldEmit(DateTime? lastEmit, TimeSpan cadence)
    {
        if (cadence <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cadence),
                "Emission cadence must be positive.");
        }

        if (lastEmit is null)
        {
            return true;
        }

        var last = lastEmit.Value;
        if (last.Kind != DateTimeKind.Utc)
        {
            last = last.Kind == DateTimeKind.Local ? last.ToUniversalTime() : DateTime.SpecifyKind(last, DateTimeKind.Utc);
        }

        return AsOf - last >= cadence;
    }
}
