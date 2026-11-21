using Microsoft.Extensions.DependencyInjection;

namespace NorthIsland.Server.Hosting;

/// <summary>Start-up wiring: creates the handlers so they register their routes and calls <c>Start</c> on those that need it.</summary>
internal static class FeatureStartup
{
    /// <summary>Creates every handler singleton and starts the ones that subscribe to events or load data.</summary>
    /// <param name="services">The built service provider.</param>
    public static void Start(IServiceProvider services)
    {
        _ = services.GetRequiredService<LoginHandlers>();
        _ = services.GetRequiredService<WorldHandlers>();
        services.GetRequiredService<EventHandlers>().Start();
        _ = services.GetRequiredService<SocialHandlers>();
        _ = services.GetRequiredService<ItemHandlers>();
        _ = services.GetRequiredService<VehicleHandlers>();
        _ = services.GetRequiredService<ShopHandlers>();
        services.GetRequiredService<OpenServices>().Start();
        _ = services.GetRequiredService<BankHandlers>();
        _ = services.GetRequiredService<ActivityHandlers>();
        _ = services.GetRequiredService<ItemEffectHandlers>();
        services.GetRequiredService<BodyShiftHandlers>().Start();
        _ = services.GetRequiredService<TrainingIslandHandlers>();
        _ = services.GetRequiredService<FurMailHandlers>();
        _ = services.GetRequiredService<ExpressHandlers>();
        _ = services.GetRequiredService<ReferHandlers>();
        _ = services.GetRequiredService<CapsuleHandlers>();
        _ = services.GetRequiredService<TransmitHandlers>();
        _ = services.GetRequiredService<OnlineDrawHandlers>();
        _ = services.GetRequiredService<ForageHandlers>();
        _ = services.GetRequiredService<ParkingHandlers>();
        _ = services.GetRequiredService<FuelBankHandlers>();
        services.GetRequiredService<BedHandlers>().Start();
        services.GetRequiredService<EggMachineHandlers>().Start();
        services.GetRequiredService<HomeShopHandlers>().Start();
        services.GetRequiredService<StallCaseHandlers>().Start();
        services.GetRequiredService<GarageHandlers>().Start();
        services.GetRequiredService<StoreroomHandlers>().Start();
        services.GetRequiredService<HouseDecorHandlers>().Start();
        services.GetRequiredService<HouseRoomHandlers>().Start();
        _ = services.GetRequiredService<MiniGridHandlers>();
        services.GetRequiredService<PillarHandlers>().Start();
        _ = services.GetRequiredService<HouseFunHandlers>();
        _ = services.GetRequiredService<FishTankHandlers>();
        services.GetRequiredService<GasHandlers>().Start();
        _ = services.GetRequiredService<UtensilStallHandlers>();
        services.GetRequiredService<BlackboardHandlers>().Start();
        services.GetRequiredService<BookHandlers>().Start();
        _ = services.GetRequiredService<GuildEconomyHandlers>();
        _ = services.GetRequiredService<TimeIconHandlers>();
        _ = services.GetRequiredService<PresentCaseHandlers>();
        _ = services.GetRequiredService<GpsHandlers>();
        services.GetRequiredService<GobangHandlers>().Start();
        services.GetRequiredService<PuppetHandlers>().Start();
        _ = services.GetRequiredService<MaidHandlers>();
        services.GetRequiredService<SpaceContractHandlers>().Start();
        _ = services.GetRequiredService<VehicleRepairHandlers>();
        _ = services.GetRequiredService<ShowItemHandlers>();
        _ = services.GetRequiredService<BlocklistHandlers>();
        _ = services.GetRequiredService<LostPropertyHandlers>();
        _ = services.GetRequiredService<RepairHandlers>();
        _ = services.GetRequiredService<AutoPlayHandlers>();
        _ = services.GetRequiredService<FusionHandlers>();
        _ = services.GetRequiredService<PoseHandlers>();
        _ = services.GetRequiredService<ConfuseStoneHandlers>();
        _ = services.GetRequiredService<DismantleHandlers>();
        _ = services.GetRequiredService<MagnetHandlers>();
        _ = services.GetRequiredService<PortableBoxHandlers>();
        _ = services.GetRequiredService<WishItemHandlers>();
        _ = services.GetRequiredService<PotentialHandlers>();
        _ = services.GetRequiredService<AtmHandlers>();
        services.GetRequiredService<InnHandlers>().Start();
        _ = services.GetRequiredService<TradeHandlers>();
        services.GetRequiredService<BattleHandlers>().Start();
        _ = services.GetRequiredService<StatHandlers>();
        _ = services.GetRequiredService<PetHandlers>();
        _ = services.GetRequiredService<FriendHandlers>();
        _ = services.GetRequiredService<MapUseHandlers>();
        services.GetRequiredService<TeamHandlers>().Start();
        _ = services.GetRequiredService<PetTradeHandlers>();
        _ = services.GetRequiredService<StallHandlers>();
        _ = services.GetRequiredService<CompoundHandlers>();
        _ = services.GetRequiredService<DyeHandlers>();
        _ = services.GetRequiredService<BoardHandlers>();
        services.GetRequiredService<RankingHandlers>().Start();
        _ = services.GetRequiredService<MallHandlers>();
        _ = services.GetRequiredService<CarnivalHandlers>();
        _ = services.GetRequiredService<PetStallHandlers>();
        _ = services.GetRequiredService<PetStorageHandlers>();
        _ = services.GetRequiredService<FishingHandlers>();
        services.GetRequiredService<HotSpringHandlers>().Start();
        services.GetRequiredService<GuildHandlers>().Start();
        services.GetRequiredService<TentHandlers>().Start();
        services.GetRequiredService<ManufactureHandlers>().Start();
        services.GetRequiredService<QuizHandlers>().Start();
        services.GetRequiredService<CompassHandlers>().Start();
        services.GetRequiredService<InstanceHandlers>().Start();
        _ = services.GetRequiredService<CareerHandlers>();
        services.GetRequiredService<MarriageHandlers>().Start();
        _ = services.GetRequiredService<RebornWishHandlers>();
        services.GetRequiredService<HouseLifeHandlers>().Start();
        _ = services.GetRequiredService<MallBoxHandlers>();
        _ = services.GetRequiredService<TeleportHandlers>();
        _ = services.GetRequiredService<PetVoucherHandlers>();
        _ = services.GetRequiredService<GiftCardHandlers>();
        services.GetRequiredService<HouseFurnitureHandlers>().Start();
    }
}
