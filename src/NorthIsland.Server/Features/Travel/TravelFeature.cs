using Microsoft.Extensions.DependencyInjection;

namespace NorthIsland.Server.Features.Travel;

/// <summary>Registers the travel feature: the teleport rule, teleport items, transmit vehicles, GPS and the Magic Magnet.</summary>
public static class TravelFeature
{
    /// <summary>
    /// Binds the travel and chat options (their sections sit under the server section) and adds the travel rule and handlers as singletons.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddTravel(this IServiceCollection services)
    {
        services.AddOptions<TravelOptions>().BindConfiguration($"{ServerOptions.Section}:{TravelOptions.Section}");
        services.AddOptions<ChatOptions>().BindConfiguration($"{ServerOptions.Section}:{ChatOptions.Section}");
        services.AddSingleton<TravelRules>();
        services.AddSingleton<TeleportHandlers>();
        services.AddSingleton<TransmitRoutes>();
        services.AddSingleton<TransmitHandlers>();
        services.AddSingleton<GpsHandlers>();
        services.AddSingleton<MagnetHandlers>();
        return services;
    }
}
