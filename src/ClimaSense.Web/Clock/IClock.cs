namespace ClimaSense.Web.Clock;

/// <summary>
/// Single source of "now" for the .NET tier. Every wall-time read goes
/// through this interface — see ADR-0004 (Replay mode with IClock) and
/// CONTEXT.md (CursorSnapshot section).
/// </summary>
/// <remarks>
/// Slice 1 ships <see cref="WallClock"/> only. <c>ReplayClock</c> arrives
/// in slice 12 (issue #14); the registration site in <c>Program.cs</c>
/// carries a <c>TODO(slice-12)</c> comment marking its eventual landing
/// point.
/// </remarks>
public interface IClock
{
    /// <summary>Returns the current logical instant in UTC.</summary>
    DateTime UtcNow();
}
