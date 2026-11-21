using Microsoft.Extensions.Logging;
using NorthIsland.Protocol.Messages.Items;
using NorthIsland.Protocol.Opcodes;

namespace NorthIsland.Server.Features.Events;

/// <summary>The service windows an NPC script can open (result kind 7), by service code.</summary>
/// <param name="inn">The inn handlers.</param>
/// <param name="petStorage">The pet storage handlers.</param>
/// <param name="atm">The ATM handlers.</param>
/// <param name="guilds">The guild handlers.</param>
/// <param name="options">The event options.</param>
/// <param name="logger">The log.</param>
public sealed class Services(
    InnHandlers inn,
    PetStorageHandlers petStorage,
    AtmHandlers atm,
    GuildHandlers guilds,
    Microsoft.Extensions.Options.IOptions<EventOptions> options,
    ILogger<Services> logger)
{
    /// <summary>Service code of the window for selling equipment.</summary>
    public const int SellEquipment = 1;

    /// <summary>Service code of the window for selling goods.</summary>
    public const int SellGoods = 2;

    /// <summary>Service code of the bank's item window.</summary>
    public const int BankItems = 4;

    /// <summary>Service code of the pet storage.</summary>
    public const int PetStorage = 5;

    /// <summary>Service code of the bank's money window.</summary>
    public const int BankMoney = 6;

    /// <summary>Service code of the inn's offer of a rest.</summary>
    public const int InnRest = 7;

    /// <summary>Service code of the notice board, which the event engine opens.</summary>
    public const int NoticeBoard = BoardHandlers.NoticeBoardService;

    /// <summary>Service code of the Top 100 list, which the event engine opens.</summary>
    public const int Top100 = BoardHandlers.Top100Service;

    /// <summary>Service code of the warehouse, which the event engine opens as the Item Bag.</summary>
    public const int Warehouse = 9;

    /// <summary>Service code of the ATM deposit form.</summary>
    public const int AtmDeposit = 17;

    /// <summary>Service code of the ATM transfer form.</summary>
    public const int AtmTransfer = 18;

    /// <summary>Service code of the Reborn Wish, which the event engine handles.</summary>
    public const int RebornWish = 8;

    /// <summary>Service code of the parking window, which the event engine handles.</summary>
    public const int Parking = 14;

    /// <summary>Service code of the vehicle repair window, which the event engine handles.</summary>
    public const int VehicleRepair = 16;

    /// <summary>Service code of the lost property window, which the event engine handles.</summary>
    public const int LostProperty = 24;

    /// <summary>Service code of the guild creation form.</summary>
    public const int Guild = 3;

    /// <summary>Service code of the PvP hall, which sends the player to the PvP server when enabled.</summary>
    public const int PvpHall = 26;

    /// <summary>Service code of the point exchange, which the event engine handles.</summary>
    public const int PointExchange = 22;

    /// <summary>Tells whether a service code is one the server has a window for.</summary>
    /// <param name="code">The service code.</param>
    /// <returns>True for the codes listed in this class.</returns>
    public static bool IsSupported(int code) => code is SellEquipment or SellGoods or BankItems or PetStorage or BankMoney or InnRest or NoticeBoard or Top100
        or AtmDeposit or AtmTransfer or PointExchange or Warehouse or Parking or VehicleRepair or LostProperty or PvpHall or Guild;

    /// <summary>Opens the window of a service for the player.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="code">The service code.</param>
    /// <returns>True when this class opened a window; false for a code it does not open itself or a disabled PvP hall.</returns>
    public bool Open(GameSession session, int code)
    {
        switch (code)
        {
            case SellEquipment: // window to sell equipment
            case SellGoods: // window to sell goods
                OpenServices.Open(session, session.ActiveCharacter!, code == SellEquipment ? ServiceWindow.SellEquipment : ServiceWindow.SellGoods);
                session.Send(ShopMessages.OpenSell(equipment: code == SellEquipment));
                logger.LogInformation("[{Session}] sell window ({Kind})", session.Id, code == SellEquipment ? "equipment" : "goods");
                return true;
            case BankItems: // bank item window
                BankHandlers.OpenItems(session, session.ActiveCharacter!);
                logger.LogInformation("[{Session}] bank items ({Count} stack(s))", session.Id, session.ActiveCharacter!.Bank.Count);
                return true;
            case BankMoney: // bank money window
                BankHandlers.OpenMoney(session, session.ActiveCharacter!);
                logger.LogInformation("[{Session}] bank money ({Gold} gold)", session.Id, session.ActiveCharacter!.BankGold);
                return true;
            case PetStorage: // pet storage
                petStorage.Open(session, session.ActiveCharacter!);
                return true;
            case InnRest: // inn offer of a rest
                inn.OfferRest(session, session.ActiveCharacter!);
                return true;
            case AtmDeposit: // ATM deposit form
            case AtmTransfer: // ATM transfer form
                atm.OpenForm(session, code == AtmDeposit ? (byte)1 : (byte)2); // form
                return true;
            case Guild: // guild creation form
                guilds.OpenCreateForm(session);
                return true;
            case PvpHall: // PvP hall: sends the player to the PvP server when enabled
                if (!options.Value.PvpHall.Enabled)
                    return false;
                session.Send(new NorthIsland.Protocol.IO.PacketWriter(Action87.Code, Action87.Relogin).U8(Action87.ToPvp).ToArray());
                logger.LogInformation("[{Session}] sent to the PvP server (87/1 1)", session.Id);
                return true;
            default: // not a window this class opens
                return false;
        }
    }
}
