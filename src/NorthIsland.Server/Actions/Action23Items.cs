using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NorthIsland.Protocol.Messages.Items;
using NorthIsland.Protocol.Opcodes;

namespace NorthIsland.Server.Actions;

/// <summary>Registers the routes of the packets the client sends with action 23: items and the many features behind them.</summary>
/// <param name="fishing">The <c>FishingHandlers</c> that serve the packets of this action.</param>
/// <param name="hotSpring">The <c>HotSpringHandlers</c> that serve the packets of this action.</param>
/// <param name="autoPlay">The <c>AutoPlayHandlers</c> that serve the packets of this action.</param>
/// <param name="battle">The <c>BattleHandlers</c> that serve the packets of this action.</param>
/// <param name="trainingIsland">The <c>TrainingIslandHandlers</c> that serve the packets of this action.</param>
/// <param name="compound">The <c>CompoundHandlers</c> that serve the packets of this action.</param>
/// <param name="confuseStone">The <c>ConfuseStoneHandlers</c> that serve the packets of this action.</param>
/// <param name="dismantle">The <c>DismantleHandlers</c> that serve the packets of this action.</param>
/// <param name="fusion">The <c>FusionHandlers</c> that serve the packets of this action.</param>
/// <param name="pose">The <c>PoseHandlers</c> that serve the packets of this action.</param>
/// <param name="fishTank">The <c>FishTankHandlers</c> that serve the packets of this action.</param>
/// <param name="houseFurniture">The <c>HouseFurnitureHandlers</c> that serve the packets of this action.</param>
/// <param name="houseLife">The <c>HouseLifeHandlers</c> that serve the packets of this action.</param>
/// <param name="manufacture">The <c>ManufactureHandlers</c> that serve the packets of this action.</param>
/// <param name="bodyShift">The <c>BodyShiftHandlers</c> that serve the packets of this action.</param>
/// <param name="dye">The <c>DyeHandlers</c> that serve the packets of this action.</param>
/// <param name="giftCard">The <c>GiftCardHandlers</c> that serve the packets of this action.</param>
/// <param name="itemEffects">The <c>ItemEffectHandlers</c> that serve the packets of this action.</param>
/// <param name="items">The <c>ItemHandlers</c> that serve the packets of this action.</param>
/// <param name="mapUse">The <c>MapUseHandlers</c> that serve the packets of this action.</param>
/// <param name="refer">The <c>ReferHandlers</c> that serve the packets of this action.</param>
/// <param name="repair">The <c>RepairHandlers</c> that serve the packets of this action.</param>
/// <param name="showItem">The <c>ShowItemHandlers</c> that serve the packets of this action.</param>
/// <param name="mallBox">The <c>MallBoxHandlers</c> that serve the packets of this action.</param>
/// <param name="presentCase">The <c>PresentCaseHandlers</c> that serve the packets of this action.</param>
/// <param name="forage">The <c>ForageHandlers</c> that serve the packets of this action.</param>
/// <param name="petVoucher">The <c>PetVoucherHandlers</c> that serve the packets of this action.</param>
/// <param name="potential">The <c>PotentialHandlers</c> that serve the packets of this action.</param>
/// <param name="puppet">The <c>PuppetHandlers</c> that serve the packets of this action.</param>
/// <param name="wishItem">The <c>WishItemHandlers</c> that serve the packets of this action.</param>
/// <param name="petStall">The <c>PetStallHandlers</c> that serve the packets of this action.</param>
/// <param name="stall">The <c>StallHandlers</c> that serve the packets of this action.</param>
/// <param name="utensilStall">The <c>UtensilStallHandlers</c> that serve the packets of this action.</param>
/// <param name="atm">The <c>AtmHandlers</c> that serve the packets of this action.</param>
/// <param name="portableBox">The <c>PortableBoxHandlers</c> that serve the packets of this action.</param>
/// <param name="magnet">The <c>MagnetHandlers</c> that serve the packets of this action.</param>
/// <param name="teleport">The <c>TeleportHandlers</c> that serve the packets of this action.</param>
/// <param name="capsule">The <c>CapsuleHandlers</c> that serve the packets of this action.</param>
/// <param name="fuelBank">The <c>FuelBankHandlers</c> that serve the packets of this action.</param>
/// <param name="parking">The <c>ParkingHandlers</c> that serve the packets of this action.</param>
/// <param name="vehicleRepair">The <c>VehicleRepairHandlers</c> that serve the packets of this action.</param>
/// <param name="world">The <c>WorldHandlers</c> that serve the packets of this action.</param>
/// <param name="itemData">The item definitions.</param>
/// <param name="itemOptions">The settings of the items feature.</param>
/// <param name="logger">The logger.</param>
public sealed class Action23Items(
    FishingHandlers fishing,
    HotSpringHandlers hotSpring,
    AutoPlayHandlers autoPlay,
    BattleHandlers battle,
    TrainingIslandHandlers trainingIsland,
    CompoundHandlers compound,
    ConfuseStoneHandlers confuseStone,
    DismantleHandlers dismantle,
    FusionHandlers fusion,
    PoseHandlers pose,
    FishTankHandlers fishTank,
    HouseFurnitureHandlers houseFurniture,
    HouseLifeHandlers houseLife,
    ManufactureHandlers manufacture,
    BodyShiftHandlers bodyShift,
    DyeHandlers dye,
    GiftCardHandlers giftCard,
    ItemEffectHandlers itemEffects,
    ItemHandlers items,
    MapUseHandlers mapUse,
    ReferHandlers refer,
    RepairHandlers repair,
    ShowItemHandlers showItem,
    MallBoxHandlers mallBox,
    PresentCaseHandlers presentCase,
    ForageHandlers forage,
    PetVoucherHandlers petVoucher,
    PotentialHandlers potential,
    PuppetHandlers puppet,
    WishItemHandlers wishItem,
    PetStallHandlers petStall,
    StallHandlers stall,
    UtensilStallHandlers utensilStall,
    AtmHandlers atm,
    PortableBoxHandlers portableBox,
    MagnetHandlers magnet,
    TeleportHandlers teleport,
    CapsuleHandlers capsule,
    FuelBankHandlers fuelBank,
    ParkingHandlers parking,
    VehicleRepairHandlers vehicleRepair,
    WorldHandlers world,
    ItemData itemData,
    IOptions<ItemOptions> itemOptions,
    ILogger<Action23Items> logger
) : IAction
{
    public byte Code => Action23.Code;

    public void Register(ActionRoutes routes)
    {
        routes
            .On(Action23.PickUp, "pick up", items.OnPickUp) // 23/2: pick up a thing from the ground
            .On(Action23.Drop, "drop", items.OnDrop) // 23/3: drop a bag item on the ground
            .On(Action23.BagMove, "move bag item", items.OnMove) // 23/10: move an item to another bag slot
            .On(Action23.Equip, "equip", items.OnEquip) // 23/11: put on a piece from the bag
            .On(Action23.Unequip, "unequip", items.OnUnequip) // 23/12: take a worn piece off into the bag
            .On(Action23.DiscardEquipped, "discard worn item", items.OnDiscardEquipped) // 23/13: destroy a worn piece
            .On(Action23.Use, "use item", items.OnUse) // 23/15: use an item from the bag
            .On(Action23.ThrowMoney, "throw money", items.OnThrowMoney) // 23/16: throw gold on the ground
            .On(Action23.PetEquip, "pet equip", items.OnPetEquip) // 23/17: put a bag item on a pet
            .On(Action23.PetUnequip, "pet unequip", items.OnPetUnequip) // 23/18: take a piece off a pet into the bag
            .On(Action23.PetDiscardEquipped, "pet discard worn item", items.OnPetDiscardEquipped) // 23/19: destroy a piece a pet wears
            .On(Action23.HotkeyUse, "hotkey use", mapUse.OnHotkeyUse) // 23/20: use an item or skill from a hotkey
            .On(Action23.StallOpen, "open stall", stall.OnOpen) // 23/30: open an item stall
            .On(Action23.StallClose, "close stall", stall.OnClose) // 23/31: close the item stall
            .On(Action23.StallBrowse, "browse stall", stall.OnBrowse) // 23/32: look at the items on offer in a stall
            .On(Action23.StallBuy, "buy from stall", stall.OnBuy) // 23/33: buy items from a stall
            // 23/34 and 23/35 (warehouse deposit and withdraw) are not routed: only the warehouse window sends them, and the server opens the
            // warehouse as the Item Bag (action 68) instead
            .On(Action23.Dye, "dye", dye.OnDye) // 23/38: dye a part of the character
            .On(Action23.EnterWater, "enter water", hotSpring.OnEnter) // 23/51: step into the water of a hot spring
            .On(Action23.LeaveWater, "leave water", HotSpringHandlers.OnLeave) // 23/52: leave the water of a hot spring
            .On(Action23.FishStart, "start fishing", fishing.OnStart) // 23/53: start fishing with a tool from the bag
            .On(Action23.FishStop, "stop fishing", fishing.OnStop) // 23/54: stop fishing
            .On(Action23.Bathe, "bathe", houseLife.OnBathe) // 23/55: get into a tub of the house and bathe
            .On(Action23.StopBathe, "leave the tub", houseLife.OnStopBathe) // 23/56: get out of the tub
            .On(Action23.PetStallOpen, "open pet stall", petStall.OnOpen) // 23/60: open a pet stall
            .On(Action23.PetStallClose, "close pet stall", petStall.OnClose) // 23/61: close the pet stall
            .On(Action23.PetStallBrowse, "browse pet stall", petStall.OnBrowse) // 23/62: look at the pets on offer in a stall
            .On(Action23.PetStallBuy, "buy from pet stall", petStall.OnBuy) // 23/63: buy pets from a stall
            .On(Action23.UtensilStallOpen, "open large-item stall", utensilStall.OnOpen) // 23/65: open a large item stall
            .On(Action23.UtensilStallClose, "close large-item stall", utensilStall.OnClose) // 23/66: close the large item stall
            .On(Action23.UtensilStallBrowse, "browse large-item stall", utensilStall.OnBrowse) // 23/67: look at the goods on offer in a large item stall
            .On(Action23.UtensilStallBuy, "buy from large-item stall", utensilStall.OnBuy) // 23/68: buy goods from a large item stall
            .On(Action23.UseFurniture, "use furniture", houseLife.OnUse) // 23/71: sit or lie on a piece of furniture
            .On(Action23.StopFurniture, "stop using furniture", houseLife.OnStopUse) // 23/72: get up from a piece of furniture
            .On(Action23.AutoCollect, "auto collect started", world.OnAutoCollect) // 23/73: auto collecting has started
            .On(Action23.OpenSweets, "open sweets pack", mallBox.OnOpen) // 23/74: open a sweets pack from the mall
            .On(Action23.OpenBox, "open box", mallBox.OnOpen) // 23/75: open a box from the mall
            .On(Action23.AutoPlayOpen, "auto play open", (s, b) => autoPlay.Set(s, null, "opened")) // 23/76: the auto play window was opened
            .On(Action23.TalkFormReady, "talk form ready 23/77", WorldHandlers.Accept) // 23/77: the talk form is ready, accepted and ignored
            .On(Action23.AutoPlayStart, "auto play start", (s, b) => autoPlay.Set(s, true, $"started ({b.Length} settings bytes)")) // 23/78: auto play starts
            .On(Action23.AutoPlayStop, "auto play stop", (s, b) => autoPlay.Set(s, false, "stopped")) // 23/79: the player stops auto play
            .On(Action23.AutoPlayGaveUp, "auto play gave up", (s, b) => autoPlay.Set(s, false, "stopped itself")) // 23/81: auto play stopped on its own
            .On(Action23.AutoPlaySupply, "remote control supply", items.OnSupply) // 23/82: remote control supply: use an item without messages
            .On(Action23.ForgetAbility, "forget ability", itemEffects.OnForget) // 23/83: forget an ability
            .On(Action23.Repair, "repair", repair.OnRepair) // 23/84: repair worn gear with a tool
            .On(Action23.PetAbilitySwap, "pet ability swap", itemEffects.OnPetSwap) // 23/85: swap the abilities of pets
            .On(Action23.Dismantle, "dismantle", dismantle.OnDismantle) // 23/86: dismantle an item
            .On(Action23.Forage, "dog bone", forage.OnStart) // 23/88: start a search for items with the pet
            .On(Action23.MagicMapUse, "teleport item", teleport.OnUse) // 23/89: use an item that teleports to a map
            .On(Action23.Magnet, "magic magnet", magnet.OnMagnet) // 23/90: the magnet exchange between two players
            .On(Action23.ForageCancel, "cancel dog bone", ForageHandlers.OnCancel) // 23/91: cancel the pet's search
            .On(Action23.Park, "park vehicle", parking.OnPark) // 23/92: park a vehicle in a parking cell
            .On(Action23.Redeem, "redeem vehicle", parking.OnRedeem) // 23/93: take a parked vehicle back into the bag
            .On(Action23.TentStyle, "use a tent palette", TentHandlers.OnStyleRequest) // 23/94: use a tent palette to change the tent style
            .On(Action23.VehicleRepair, "repair vehicle", vehicleRepair.OnRepair) // 23/95: repair a worn vehicle in the bag
            .On(Action23.UseVoucher, "pet voucher", petVoucher.OnUse) // 23/96: use a pet voucher
            .On(Action23.FishFeed, "fish tank click", FishTankHandlers.OnFeed) // 23/97: feed the fish in the tank
            .On(Action23.FishAct, "fish tank request", fishTank.OnRequest) // 23/98: ask for the fish buy list or throw-away list, buy or throw one
            .On(Action23.PianoSit, "piano seat", houseFurniture.OnPianoSit) // 23/99: sit down at the piano
            .On(Action23.Refer, "magnifying glass", refer.OnSearch) // 23/103: search with a magnifying glass
            .On(Action23.Puppet, "memory card", puppet.OnPacket) // 23/104: a packet of the memory card puppet
            .On(Action23.Pose, "pose", pose.OnPose) // 23/105: strike a pose
            .On(Action23.WinPlate, "win plate", battle.OnWinPlate) // 23/106: use a win plate in a battle
            .On(Action23.DelayClock, "delay clock", battle.OnDelayClock) // 23/107: use a delay clock in a battle
            .On(Action23.TimeMantle, "time mantle", manufacture.OnTimeMantle) // 23/108: use a time mantle on a manufacturing job
            .On(Action23.PointExchange, "point exchange", atm.OnPointExchange) // 23/109: exchange points at an ATM
            .On(Action23.Stopwatch, "stopwatch", itemEffects.OnStopwatch) // 23/110: use a stopwatch
            .On(Action23.TrainingIsland, "training island", trainingIsland.OnRequest) // 23/111: a training island request
            .On(Action23.Packing, "present case", presentCase.OnPacking) // 23/112: pack items in a present case
            .On(Action23.BodyShift, "body shift", bodyShift.OnBodyShift) // 23/114: use a body shift item
            .On(Action23.Throw, "throw item", showItem.OnThrow) // 23/117: throw an item at a point of the map
            .On(Action23.FlowerMessage, "passion bouquet", (s, b) => wishItem.OnSend(s, b, lamp: false)) // 23/118: send a flower message with a passion bouquet
            .On(Action23.StatBuff, "stat buff", itemEffects.OnStatBuff) // 23/119: use an item that gives a stat buff
            .On(Action23.OpenGiftBox, "open mall gift box", mallBox.OnOpenGift) // 23/120: open a gift box from the mall
            .On(Action23.Reinforce, "fusion", fusion.OnFusion) // 23/121: fuse items to reinforce
            .On(Action23.Discard, "discard", items.OnDiscard) // 23/124: destroy bag items
            .On(Action23.Capsule, "space capsule", capsule.OnConfirm) // 23/125: confirm the use of a space capsule
            .On(Action23.Potential, "potential pill", potential.OnPotential) // 23/126: use a potential pill
            .On(Action23.ConfuseStone, "chaos crystal", confuseStone.OnUse) // 23/127: use a chaos crystal
            .On(Action23.OpenBox2, "open box", mallBox.OnOpen) // 23/128: open a box from the mall
            .On(Action23.BlessMessage, "bliss lamp", (s, b) => wishItem.OnSend(s, b, lamp: true)) // 23/129: send a wish message with a bliss lamp
            .On(Action23.PotentialReturn, "anti-potential pill", potential.OnReturn) // 23/130: use an anti-potential pill on a pet
            .On(Action23.PicnicBox, "picnic box", (s, b) => portableBox.Handle(s, b, picnic: true)) // 23/131: use a picnic box
            .On(Action23.ExpBag, "exp bag", (s, b) => portableBox.Handle(s, b, picnic: false)) // 23/132: use an exp bag
            .On(Action23.GiftCard, "gift card", giftCard.OnCard) // 23/133: open or exchange gift cards
            .On(Action23.FuelBank, "fuel bank", fuelBank.OnBank) // 23/134: put fuel into, take it out of or use a fuel bank
            .On(
                Action23.QolSort,
                "qol sort",
                (s, b) => SortHandlers.Handle(s, b, itemData, [.. itemOptions.Value.BagSortFirst], logger)) // 23/250: sort the bag
            .Clears(Action23.Reinforce, () => [FusionMessages.Failed()], whenSilent: true); // 23/121: sends the fusion failed packet if no reply
        foreach (var sub in new[] { Action23.Compound, Action23.CompoundJunior, Action23.CompoundHigh, Action23.CompoundBless })
            routes.On(sub, "compound", (s, body) => compound.OnCompound(s, sub, body)); // 23/?: compound items; the sub code picks the kind
    }
}
