namespace ClimaSense.Web.Clock;

/// <summary>
/// Production-default <see cref="IClock"/> backed by <see cref="DateTime.UtcNow"/>.
/// The ONLY place in the codebase that calls <c>DateTime.UtcNow</c> directly.
/// </summary>
public sealed class WallClock : IClock
{
    public DateTime UtcNow() => DateTime.UtcNow;
}
