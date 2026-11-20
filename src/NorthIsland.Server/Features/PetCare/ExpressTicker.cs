using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NorthIsland.Server.Features.PetCare;

/// <summary>A background service that ticks the express and forage jobs of every online player once a second.</summary>
/// <param name="express">The express handlers.</param>
/// <param name="forage">The forage handlers.</param>
/// <param name="world">The world state, for the online sessions.</param>
/// <param name="logger">The logger.</param>
public sealed class ExpressTicker(
    ExpressHandlers express,
    ForageHandlers forage,
    WorldState world,
    ILogger<ExpressTicker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                foreach (var session in world.OnlineSessions().Where(s => s.PetJobs.Express is not null || s.PetJobs.Forage is not null).ToList())
                {
                    try
                    {
                        if (!session.TryExclusive(out var held)) // a packet is being handled: try again on the next tick
                            continue;
                        using (held)
                        {
                            express.Tick(session);
                            forage.Tick(session);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "express tick failed"); }
                }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
