using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NorthIsland.Server.Hosting;

/// <summary>Background service that saves every online account every <c>AutosaveMinutes</c> minutes (off when that is 0 or less).</summary>
/// <param name="world">The online sessions and accounts.</param>
/// <param name="store">The account store.</param>
/// <param name="options">The server options.</param>
/// <param name="logger">Logger for the handler's messages.</param>
public sealed class Autosave(
    WorldState world,
    IAccountStore store,
    IOptions<ServerOptions> options,
    ILogger<Autosave> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var minutes = options.Value.AutosaveMinutes;
        if (minutes <= 0)
            return;

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(minutes));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                await SaveAllAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Banks the online time of every session and saves every online account, each under its session's gate so that no packet, timer or other
    /// player changes the character while it is written. The service holds no other gate, so it may wait for each one.
    /// </summary>
    /// <param name="ct">Cancels the saves.</param>
    private async Task SaveAllAsync(CancellationToken ct)
    {
        var saved = 0;
        var now = GameClock.UtcNow;
        foreach (var session in world.OnlineSessions())
        {
            using var exclusive = await session.ExclusiveAsync().ConfigureAwait(false);
            if (session.Closing) // ended: the disconnect saves it
                continue;
            OnlineTime.Bank(session, now);
            if (session.Account is not { } account)
                continue;
            try
            {
                await store.SaveAsync(account, ct).ConfigureAwait(false);
                saved++;
            }
            catch (InvalidOperationException ex)
            {
                logger.LogDebug(ex, "Autosave of {Account} skipped", account.Name);
            }
        }
        if (saved > 0)
            logger.LogInformation("Autosaved {Count} account(s)", saved);
    }
}
