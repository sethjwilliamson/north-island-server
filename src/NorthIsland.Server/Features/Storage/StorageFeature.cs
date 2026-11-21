using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace NorthIsland.Server.Features.Storage;

/// <summary>Registers the storage feature: the open service windows, bank, ATM, Item Bag, Space Contract and portable boxes.</summary>
public static class StorageFeature
{
    /// <summary>Binds the storage options and adds the record of open service windows and the storage handlers as singletons.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="section">The configuration section of the storage options.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddStorage(this IServiceCollection services, IConfiguration section)
    {
        services.Configure<StorageOptions>(section);
        services.AddSingleton<NorthIsland.Server.Features.Storage.SpaceContract>();
        services.AddSingleton<OpenServices>();
        services.AddSingleton<BankHandlers>();
        services.AddSingleton<AtmHandlers>();
        services.AddSingleton<MiniGridHandlers>();
        services.AddSingleton<SpaceContractHandlers>();
        services.AddSingleton<PortableBoxHandlers>();
        return services;
    }
}
