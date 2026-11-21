using Microsoft.Extensions.Logging;

namespace NorthIsland.Server.Net;

/// <summary>
/// Limits how many lines of one kind a session may put in the log per time window, so that a client sending junk on purpose cannot flood the
/// log; counts the lines it holds back so that the next window can say how many were dropped.
/// </summary>
/// <param name="lines">The lines allowed per window.</param>
/// <param name="windowMs">The window in milliseconds.</param>
public sealed class LogBudget(
    int lines,
    long windowMs)
{
    /// <summary>Held while the counts change.</summary>
    private readonly Lock _lock = new();

    /// <summary>When the current window began, in <see cref="Environment.TickCount64"/> milliseconds.</summary>
    private long _windowStartMs = Environment.TickCount64;

    /// <summary>The lines logged in the current window.</summary>
    private int _used;

    /// <summary>The lines held back since the latest line that was logged.</summary>
    private int _dropped;

    /// <summary>Takes one line from the budget.</summary>
    /// <param name="dropped">When the line may be logged: the lines held back before it, to report; otherwise 0.</param>
    /// <returns>True when the line may be logged.</returns>
    public bool TryTake(out int dropped)
    {
        lock (_lock)
        {
            var now = Environment.TickCount64;
            if (now - _windowStartMs >= windowMs)
            {
                _windowStartMs = now;
                _used = 0;
            }
            if (_used >= lines)
            {
                _dropped++;
                dropped = 0;
                return false;
            }
            _used++;
            dropped = _dropped;
            _dropped = 0;
            return true;
        }
    }

    /// <summary>
    /// Takes one line from the budget of a session's log lines, first logging how many lines the budget held back; for handlers whose refusals a
    /// client can repeat at will.
    /// </summary>
    /// <param name="logger">The logger the line goes to.</param>
    /// <param name="sessionId">The session id, for the line about the held-back lines.</param>
    /// <returns>True when the line may be logged.</returns>
    public bool TryTake(ILogger logger, int sessionId)
    {
        if (!TryTake(out var dropped))
            return false;
        if (dropped > 0)
            logger.LogWarning("[{Session}] {Count} log line(s) of this session not logged", sessionId, dropped);
        return true;
    }
}
