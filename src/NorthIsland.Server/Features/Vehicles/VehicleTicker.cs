using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NorthIsland.Server.Features.Vehicles;

/// <summary>Background service that, every few seconds (<see cref="VehicleOptions.DrainTickSeconds"/>), drains the vehicle of every player who rides one.</summary>
/// <param name="care">Applies the tick to one session.</param>
/// <param name="world">The online sessions.</param>
/// <param name="options">The vehicle options, for the tick length.</param>
/// <param name="logger">Logger for the handler's messages.</param>
public sealed class VehicleTicker(
    VehicleCareHandlers care,
    WorldState world,
    IOptions<VehicleFeatureOptions> options,
    ILogger<VehicleTicker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = Math.Max(1, options.Value.Vehicles.DrainTickSeconds);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                foreach (var session in world.OnlineSessions().Where(s => s.Vehicle is { Riding: true }).ToList())
                {
                    try { care.Tick(session, seconds); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "vehicle tick failed"); }
                }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
