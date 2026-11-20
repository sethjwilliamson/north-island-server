using Microsoft.Extensions.DependencyInjection;

namespace NorthIsland.Server.Hosting;

/// <summary>Service registration of the game server.</summary>
public static class ServiceRegistration
{
    /// <summary>Registers the options, the data and world services, every feature and the hosted services, in start order.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="section">The configuration section of the server.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddGameServer(this IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration section)
    {
        services.Configure<ServerOptions>(section);
        services.Configure<WorldOptions>(section);
        services.AddStorage();
        services.AddSingleton<NorthIsland.Server.World.WorldState>();
        services.AddSingleton<NorthIsland.Server.World.MapData>();
        services.AddSingleton<NorthIsland.Server.Data.ItemData>();
        services.AddSingleton<NorthIsland.Server.Data.FormulaData>();
        services.AddSingleton<NorthIsland.Server.Data.TalkData>();
        services.AddSingleton<NorthIsland.Server.Data.NpcData>();
        services.AddSingleton<NorthIsland.Server.Data.SkillData>();
        services.AddSingleton<NorthIsland.Server.Data.MarkData>();
        services.AddSingleton<NorthIsland.Server.World.GroundItems>();
        services.AddSingleton<NorthIsland.Server.World.CombatStats>();
        services.AddSingleton<NorthIsland.Server.World.Experience>();
        services.AddSingleton<WorldHandlers>();
        services.AddSingleton<NorthIsland.Server.World.TeamVitals>();
        services.AddSingleton<GameListener>();
        services.AddSingleton<NorthIsland.Server.World.NpcWander>();
        services.AddActivities(section);
        services.AddAdmin();
        services.AddBattles(section);
        services.AddBoards();
        services.AddCrafting();
        services.AddEvents(section);
        services.AddGuilds(section);
        services.AddHouses(section);
        services.AddInn();
        services.AddInstances(section);
        services.AddItems(section);
        services.AddLogin();
        services.AddMall(section);
        services.AddMarriage(section);
        services.AddMinigames(section);
        services.AddPetCare(section);
        services.AddProgression(section);
        services.AddShops();
        services.AddSocial(section);
        services.AddStalls();
        services.AddStorage(section);
        services.AddTrading(section);
        services.AddTravel();
        services.AddVehicles(section);
        services.AddActions();
        services.AddHostedService<VehicleTicker>();
        services.AddHostedService<ExpressTicker>();
        services.AddHostedService(sp => sp.GetRequiredService<GameListener>());
        services.AddHostedService<StatusListener>();
        services.AddHostedService<NorthIsland.Server.Hosting.Autosave>();
        services.AddHostedService<NorthIsland.Server.Features.Guilds.GuildSweep>();
        services.AddHostedService<NorthIsland.Server.World.GroundRespawn>();
        services.AddHostedService<NorthIsland.Server.Features.Events.CalendarTicker>();
        services.AddHostedService<ActivityTicker>();
        services.AddHostedService<ItemEffectTicker>();
        services.AddHostedService<BodyShiftTicker>();
        services.AddHostedService<NorthIsland.Server.Features.Guilds.TrojanWarTicker>();
        services.AddHostedService<HouseLifeTicker>();
        services.AddHostedService<SittingTicker>();
        services.AddHostedService(sp => sp.GetRequiredService<NorthIsland.Server.World.NpcWander>());
        services.AddHostedService<ShutdownSignal>();
        return services;
    }
}
