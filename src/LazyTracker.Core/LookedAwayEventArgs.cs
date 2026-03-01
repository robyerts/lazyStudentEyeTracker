namespace LazyTracker.Core;

/// <summary>
/// Event args for the LookedAway event.
/// </summary>
public sealed class LookedAwayEventArgs : EventArgs
{
    /// <summary>
    /// How long (in seconds) the user has been looking away.
    /// </summary>
    public double SecondsAway { get; init; }

    /// <summary>
    /// The total number of times the user has looked away this session.
    /// </summary>
    public int TotalTriggers { get; init; }

    /// <summary>
    /// Why the trigger fired.
    /// </summary>
    public LookAwayReason Reason { get; init; }
}
