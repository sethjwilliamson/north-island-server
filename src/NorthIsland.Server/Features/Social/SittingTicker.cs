using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NorthIsland.Server.Features.Social;

/// <summary>Background service that, once a second, runs the HP and SP recovery of the sessions that sit (<see cref="SittingOptions"/>).</summary>
/// <param name="social">The social handlers that do the steps.</param>
/// <param name="world">The online sessions.</param>
/// <param name="logger">The logger.</param>
public sealed class SittingTicker(
    SocialHandlers social,
    WorldState world,
    ILogger<SittingTicker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                foreach (var session in world.OnlineSessions().Where(s => s.Sitting.Active).ToList())
                    if (session.ActiveCharacter is { } c && session.State == SessionState.InWorld)
                    {
                        try { social.SittingTick(session, c, GameClock.UtcNow); }
                        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "sitting tick failed"); }
                    }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
