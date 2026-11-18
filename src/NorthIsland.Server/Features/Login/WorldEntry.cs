using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NorthIsland.GameData.Characters;
using NorthIsland.Protocol;
using NorthIsland.Protocol.IO;
using NorthIsland.Protocol.Messages.Login;
using NorthIsland.Protocol.Messages.Player;
using NorthIsland.Protocol.Messages.World;
using NorthIsland.Protocol.Opcodes;

namespace NorthIsland.Server.Features.Login;

/// <summary>Prepares a character and sends the client everything it needs when the character enters the world.</summary>
/// <param name="store">Account storage; the account is saved on entry.</param>
/// <param name="world">The world state the session joins.</param>
/// <param name="itemHandlers">Starter kit and inventory sending.</param>
/// <param name="itemData">Item definitions.</param>
/// <param name="friends">Friend list handlers, which send their login packets.</param>
/// <param name="guilds">Guild handlers, which send the guild id, badges and their login packets.</param>
/// <param name="guildEconomy">Guild economy handlers, which send their login packets.</param>
/// <param name="puppets">Puppet handlers, which send their login packets.</param>
/// <param name="wander">Wandering NPCs, whose positions are sent.</param>
/// <param name="skills">Skill definitions.</param>
/// <param name="events">The event engine: pending role, map entry.</param>
/// <param name="marks">Mark data: normalised and sent on entry.</param>
/// <param name="ground">Items on the ground, whose list is sent.</param>
/// <param name="tents">Tent handlers: login and map entry.</param>
/// <param name="instances">Instance handlers: placement at login and their login packets.</param>
/// <param name="marriage">Marriage handlers, which send the spouse card.</param>
/// <param name="castles">Castle registry: owner packet and the war state.</param>
/// <param name="mall">Mall handlers, which send the catalog and points.</param>
/// <param name="giftCards">Gift card handlers, which send their login packets.</param>
/// <param name="onlineDraw">Online draw handlers, which send their login packets.</param>
/// <param name="calendar">The event calendar: weekly reset clock and login packets.</param>
/// <param name="effects">Item effect handlers, which send their login packets.</param>
/// <param name="combat">Combat statistics: recalculated on entry and sent.</param>
/// <param name="eventOptions">Event options (the team NPC equipment).</param>
/// <param name="tradeOptions">Trade options (the online hours needed to trade).</param>
/// <param name="progressionOptions">Progression options (points per level).</param>
/// <param name="logger">Logger.</param>
public sealed class WorldEntry(
    IAccountStore store,
    WorldState world,
    ItemHandlers itemHandlers,
    ItemData itemData,
    FriendHandlers friends,
    GuildHandlers guilds,
    GuildEconomyHandlers guildEconomy,
    PuppetHandlers puppets,
    NpcWander wander,
    SkillData skills,
    EventEngine events,
    MarkData marks,
    GroundItems ground,
    TentHandlers tents,
    InstanceHandlers instances,
    MarriageHandlers marriage,
    CastleRegistry castles,
    MallHandlers mall,
    GiftCardHandlers giftCards,
    OnlineDrawHandlers onlineDraw,
    EventCalendar calendar,
    ItemEffectHandlers effects,
    CombatStats combat,
    IOptions<EventOptions> eventOptions,
    IOptions<TradeOptions> tradeOptions,
    IOptions<ProgressionOptions> progressionOptions,
    ILogger<WorldEntry> logger)
{
    /// <summary>
    /// Puts the character into the world: applies pending fixes (starting attributes, owed points, weekly reset, return from an event arena), saves
    /// the account, sends the own record, attributes, pets, inventory, options, marks and every feature's login packets, then joins the world.
    /// </summary>
    /// <param name="session">The session of the player.</param>
    /// <param name="character">The character that enters.</param>
    internal async Task EnterWorldAsync(GameSession session, Character character)
    {
        var account = session.Account!;
        session.ActiveCharacter = character;
        itemHandlers.EnsureStarterKit(character);
        events.TakePendingRole(session, character);
        if (!character.AttributesFromCreation)
            ApplyStartingAttributes(character);
        combat.Recalculate(character);
        GrantMissingPoints(character);
        tents.OnLogin(character, account.LoginId);
        if (EventArenas.IsArena(character.MapId))
            EventArenas.SendBack(character);
        if (EventArenas.WeeklyReset(character, calendar.Clock()))
            logger.LogInformation(
                "[{Session}] {Name}: the weekly activity flags are cleared (week {Week})",
                session.Id,
                character.Name,
                character.ActivityWeek);
        instances.PlaceAtLogin(character, account.LoginId);
        if (character.Skill(RoleData.NewbieSkill) is null && skills[RoleData.NewbieSkill] is not null)
            character.Skills.Add(new LearnedSkill { Id = RoleData.NewbieSkill });
        session.State = SessionState.InWorld;
        session.ArrivedUtc = GameClock.UtcNow;
        await store.SaveAsync(account).ConfigureAwait(false);

        session.Send(ServerMessages.OwnPlayer(new OwnPlayerRecord
        {
            Id = account.LoginId,
            BodyType = character.BodyType,
            MapId = character.MapId,
            X = character.X,
            Y = character.Y,
            Head = character.Head,
            Sprite = character.Sprite,
            Colour1 = character.Colour1,
            Colour2 = character.Colour2,
            EquippedItems = [.. character.EquippedItemIds()],
            PvpWins = character.PvpWins,
            PvpRank = character.PvpRank,
            Name = GmTag.Shown(character.Name, account),
            Nickname = character.Nickname,
            BloodType = character.BloodType,
            BirthYear = character.BirthYear,
            BirthMonth = character.BirthMonth,
            BirthDay = character.BirthDay,
        }));

        if (session.Client == ClientGeneration.Client2014)
            session.Send(ServerMessages.EnterGameDone());

        session.Send(PlayerMessages.OwnAttributes(new PlayerAttributes
        {
            GuildId = guilds.IdOf(session.Account!.LoginId),
            Element = character.Element,
            Hp = Math.Max(0, character.Hp),
            Sp = (ushort)Math.Clamp(character.Sp, 0, ushort.MaxValue),
            Strength = character.Strength,
            Constitution = character.Constitution,
            Intelligence = character.Intelligence,
            Wisdom = character.Wisdom,
            Agility = character.Agility,
            Level = character.Level,
            Exp = character.Exp,
            SkillPoints = character.SkillPoints,
            StatPoints = character.StatPoints,
            MaxHp = Math.Max(0, character.MaxHp),
            MaxSp = (ushort)Math.Clamp(character.MaxSp, 0, ushort.MaxValue),
            MaxHpBonus = combat.MaxBonus(character, CombatStats.AttrMaxHpBonus),
            MaxSpBonus = combat.MaxBonus(character, CombatStats.AttrMaxSpBonus),
            Skills = skills.Entries(character),
            Reborn = character.RebornCount,
            Job = character.Job,
            Potential = character.Potential,
        }, session.Client));
        combat.Send(session);
        session.Send(PlayerMessages.Money(character.Gold));
        foreach (var pet in character.Pets)
            Pets.GiveStartingGear(pet, eventOptions.Value.TeamNpcEquipment, id => itemData[id]);
        if (character.Pets.Count > 0)
            session.Send(PetMessages.List(character.Pets.Select(Pets.Info), session.Client));
        foreach (var pet in character.Pets.Where(p => p.Equipment.Count > 0))
            combat.SendPetBonuses(session, pet, changed: false);
        PetStorageHandlers.SendList(session, character);
        if (character.ActivePetId != 0 && !character.Pets.Exists(p => p.NpcId == character.ActivePetId))
            character.ActivePetId = 0;
        if (character.ActivePetId != 0)
            session.Send(PetMessages.Selected(character.ActivePetId));
        if (character.RidingPetSlot != 0) // still on the pet ridden at logout: mounted as by 15/11 (the map is told by world.Enter)
        {
            if (character.Pets.Find(p => p.Slot == character.RidingPetSlot) is { } ridden)
            {
                session.Send(PetMessages.Mounted(ridden.Slot, session.Account!.LoginId, ridden.NpcId));
                combat.Send(session);
            }
            else
                character.RidingPetSlot = 0;
        }

        Bag.SplitLegacyStacks(character, itemData);
        ItemHandlers.SendInventory(session, character);
        giftCards.SendOnLogin(session);
        onlineDraw.SendOnLogin(session, character);

        mall.SendOnLogin(session, session.Account!);

        session.Send(WorldServerMessages.OptionState(character.Option1, character.Option2, character.Option6,
            character.PartyOption == 0 ? (byte)1 : character.PartyOption, character.ChannelBits, session.Client));

        OnlineTime.Announce(session, world, tradeOptions.Value.TradeOnlineHours);

        marks.Normalize(character);
        marks.SendAll(session, character);

        session.Send(PlayerMessages.SceneReady());

        guilds.SendBadgeList(session, castleToo: false);
        session.Send(castles.OwnerPacket());

        session.Send(new PacketWriter(Action20.Code, Action20.Finished).ToArray());

        world.Enter(session);

        friends.SendOnLogin(session, character);
        FurMailHandlers.SendOnLogin(session, character);
        BlocklistHandlers.SendOnLogin(session, character);
        PortableBoxHandlers.SendOnLogin(session, character);
        guilds.SendOnLogin(session, character);
        guildEconomy.SendOnLogin(session);
        GpsHandlers.SendMarks(session, character);
        puppets.SendOnLogin(session, character);
        marriage.SendOnLogin(session, character);
        calendar.SendOnLogin(session);
        effects.SendOnLogin(session, character);
        GmCommands.SendOnLogin(session);
        if (castles.WarOn)
            session.Send(TrojanWar.Message(TrojanWar.EventBegins));
        AtmHandlers.SendOnLogin(session, character);
        if (character.RecordMapId != 0)
            session.Send(new PacketWriter(Action5.Code, Action5.ReturnSpotRecorded).U8(1).ToArray());
        if (character.Hotkeys.Count > 0)
            session.Send(PlayerMessages.Hotkeys(character.Hotkeys.Select(h => (h.Kind, h.Id, h.Page, h.Slot))));

        ground.SendList(session);

        tents.MapEntered(session);

        wander.SendPositions(session);
        events.EnterMap(session);
        instances.SendOnLogin(session);
    }

    /// <summary>
    /// Sets the five attributes from the stats chosen at creation and the character's role; does nothing when fewer than five creation stats are
    /// stored.
    /// </summary>
    /// <param name="character">The character to set up.</param>
    internal void ApplyStartingAttributes(Character character)
    {
        if (character.CreationStats.Count < 5)
            return;
        var a = RoleData.StartingAttributes(character.CreationStats, RoleData.RoleIndex(character.BodyType, character.Head));
        character.Strength = a.Strength;
        character.Constitution = a.Constitution;
        character.Intelligence = a.Intelligence;
        character.Wisdom = a.Wisdom;
        character.Agility = a.Agility;
        character.AttributesFromCreation = true;
        logger.LogInformation("{Name}: STR {Str} CON {Con} INT {Int} WIS {Wis} AGI {Agi}",
            character.Name, a.Strength, a.Constitution, a.Intelligence, a.Wisdom, a.Agility);
    }

    /// <summary>
    /// Adds stat points when the attributes plus free points fall short of the creation total plus the per-level grant, and raises skill points to
    /// the per-level total.
    /// </summary>
    /// <param name="c">The character to check.</param>
    private void GrantMissingPoints(Character c)
    {
        var levels = Math.Max(0, c.Level - 1);
        if (c.CreationStats.Count >= 5)
        {
            var start = RoleData.StartingAttributes(c.CreationStats, RoleData.RoleIndex(c.BodyType, c.Head));
            var startSum = start.Strength + start.Constitution + start.Intelligence + start.Wisdom + start.Agility;
            var have = c.Strength + c.Constitution + c.Intelligence + c.Wisdom + c.Agility + c.StatPoints;
            var missing = startSum + levels * progressionOptions.Value.StatPointsPerLevel - have;
            if (missing > 0)
            {
                c.StatPoints = (ushort)Math.Min(ushort.MaxValue, c.StatPoints + missing);
                logger.LogInformation("{Name}: +{Missing} stat points owed for level {Level} ({Points} now)", c.Name, missing, c.Level, c.StatPoints);
            }
        }
        var skillPoints = levels * progressionOptions.Value.SkillPointsPerLevel;
        if (c.SkillPoints < skillPoints)
        {
            logger.LogInformation("{Name}: skill points {Old} -> {New} for level {Level}", c.Name, c.SkillPoints, skillPoints, c.Level);
            c.SkillPoints = (ushort)Math.Min(ushort.MaxValue, skillPoints);
        }
    }
}
