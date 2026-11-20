using Microsoft.Extensions.Logging;
using NorthIsland.GameData.Events;
using NorthIsland.Protocol.Messages.Events;
using NorthIsland.Protocol.Opcodes;

namespace NorthIsland.Server.Features.Events;

/// <summary>
/// Runs the NPC and door scripts (eve events): finds the condition groups that hold, turns their results into packets and server changes,
/// and steps through them as the client asks for the next result.
/// </summary>
/// <param name="maps">The map data.</param>
/// <param name="world">The world state.</param>
/// <param name="talk">The dialog texts.</param>
/// <param name="items">The item data.</param>
/// <param name="npcs">The NPC data.</param>
/// <param name="marks">The quest mark data.</param>
/// <param name="services">The service windows scripts can open.</param>
/// <param name="formulas">The formula data.</param>
/// <param name="tents">The tent and house handlers.</param>
/// <param name="experience">The experience rules.</param>
/// <param name="instances">The instance registry.</param>
/// <param name="instanceHandlers">The instance handlers.</param>
/// <param name="career">The career handlers.</param>
/// <param name="rebornWish">The Reborn Wish handlers.</param>
/// <param name="marriage">The marriage handlers.</param>
/// <param name="boards">The board handlers.</param>
/// <param name="itemBag">The Item Bag handlers.</param>
/// <param name="mall">The mall handlers.</param>
/// <param name="atm">The ATM handlers.</param>
/// <param name="castles">The castle registry.</param>
/// <param name="guilds">The guild registry.</param>
/// <param name="calendar">The event calendar.</param>
/// <param name="combat">The combat stats, resent after a role change.</param>
/// <param name="skills">The skill data, for the skills scripts teach.</param>
/// <param name="lifeSkills">The life skills, trained by gathering.</param>
/// <param name="options">The event options.</param>
/// <param name="logger">The log.</param>
public sealed partial class EventEngine(
    MapData maps,
    WorldState world,
    TalkData talk,
    ItemData items,
    NpcData npcs,
    MarkData marks,
    Services services,
    FormulaData formulas,
    TentHandlers tents,
    Experience experience,
    InstanceRegistry instances,
    InstanceHandlers instanceHandlers,
    CareerHandlers career,
    RebornWishHandlers rebornWish,
    MarriageHandlers marriage,
    BoardHandlers boards,
    MiniGridHandlers itemBag,
    MallHandlers mall,
    AtmHandlers atm,
    CastleRegistry castles,
    GuildRegistry guilds,
    EventCalendar calendar,
    CombatStats combat,
    SkillData skills,
    LifeSkills lifeSkills,
    Microsoft.Extensions.Options.IOptions<EventOptions> options,
    Microsoft.Extensions.Options.IOptions<Items.ItemOptions> itemOptions,
    ILogger<EventEngine> logger)
{
    /// <summary>The NPC action that is the standing pose.</summary>
    public const byte StandingAction = 8;

    /// <summary>Trigger code (action 20 sub code) for a click on an NPC.</summary>
    public const byte TriggerClick = 1;

    /// <summary>Trigger code (action 20 sub code) for stepping on an area; field encounters also start their runs with it.</summary>
    public const byte TriggerArea = 3;

    /// <summary>Trigger code (action 20 sub code) for a click on a door.</summary>
    public const byte TriggerDoorClick = 4;

    /// <summary>Trigger code (action 20 sub code) for walking into a door.</summary>
    public const byte TriggerDoorWalk = 8;

    /// <summary>
    /// Starts the event of a clicked NPC or a door the player used: queues its condition groups and runs the first. Inside a house the door
    /// leaves the house instead.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="trigger">The trigger code.</param>
    /// <param name="index">The placement index of the NPC or door on the map.</param>
    public void Start(GameSession session, byte trigger, int index)
    {
        var character = session.ActiveCharacter!;
        if (session.Event is { } previous)
            logger.LogInformation("[{Session}] new trigger replaces the running event of {Source}", session.Id, previous.Source);
        session.Event = null;

        if (TentHandlers.IsHouseMap(character.MapId))
        {
            if (trigger is TriggerDoorClick or TriggerDoorWalk)
                tents.LeaveHouse(session);
            else
                session.Send(EventMessages.Finished());
            return;
        }

        var scene = maps.Scene(character.MapId);
        EvePlacedObject? source = trigger is TriggerDoorClick or TriggerDoorWalk
            ? scene?.DoorByIndex(index)
            : scene?.NpcByRoleIndex(index);
        if (scene is null || source is null)
        {
            logger.LogInformation("[{Session}] trigger {Trigger} index {Index}: nothing on map {Map}", session.Id, trigger, index, character.MapId);
            session.Send(EventMessages.Finished());
            return;
        }

        var name = source is EveNpc npc ? $"NPC {index} (npc {npc.NpcId})" : $"door {index}";
        var run = NewRun(session, character, trigger, index, name);
        foreach (var ev in scene.EventsOf(source))
            foreach (var group in ConditionEvaluator.Groups(ev))
                run.Groups.Enqueue(group);
        logger.LogInformation("[{Session}] {Source} on map {Map}: {Count} condition group(s)", session.Id, name, character.MapId, run.Groups.Count);
        session.Event = run;
        if (trigger == TriggerClick && source is EveNpc box && IsTreasureBox(character.MapId, box.NpcId))
        {
            session.Send(EventMessages.TreasureJump((ushort)index));
            logger.LogInformation("[{Session}] {Source}: the treasure box jumps (20/46)", session.Id, name);
            return;
        }
        Advance(session);
    }

    /// <summary>Creates an event run for the player on their map, with their team, instance, guild and castle state.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="character">The character.</param>
    /// <param name="trigger">The trigger code.</param>
    /// <param name="index">The placement index of what started the run.</param>
    /// <param name="name">The name of the source, for the log.</param>
    /// <returns>The new run; it is not yet attached to the session.</returns>
    private EventRun NewRun(
        GameSession session,
        Character character,
        byte trigger,
        int index,
        string name) =>
            new(character.MapId, trigger, index, name)
            {
                NpcStates = session.NpcStates,
                IsCounter = marks.IsCounter,
                TeamPlayers = Math.Max(1, world.Teams.TeamOf(session.Account!.LoginId).Count),
                Instance = instances.ByMap(character.MapId),
                PlayerId = session.Account!.LoginId,
                CastleGuildMember = castles.IsOwnerMember(session.Account!.LoginId),
                Castles = castles,
                GuildId = guilds.Of(session.Account!.LoginId)?.Id ?? 0,
                TeamWithoutGuild = TeamOrSelf(session.Account!.LoginId).Count(id => guilds.Of(id) is null),
                ScriptNow = ScriptClock(character.MapId),
                Clock = () => calendar.Clock(),
                RidingVehicle = session.Vehicle is { Riding: true } vehicle ? vehicle.ItemId : (ushort)0,
                RoomFor = id => World.Bag.IsStackable(items[id]) && character.Bag.Exists(b => b.ItemId == id && b.Count < World.Bag.MaxStack)
                    || World.Bag.FreeSlot(character, items, id) != 0,
            };

    /// <summary>Lists the ids of the players in a player's team.</summary>
    /// <param name="playerId">The player's id.</param>
    /// <returns>The team, or just the player when they have none.</returns>
    private IReadOnlyList<int> TeamOrSelf(int playerId) => world.Teams.TeamOf(playerId) is { Count: > 0 } team ? team : [playerId];

    /// <summary>Handles the client's request for the next result: goes on with the running event, or finishes at once when none runs.</summary>
    /// <param name="session">The player's session.</param>
    public void Next(GameSession session)
    {
        if (session.Event is null)
        {
            session.Send(EventMessages.Finished());
            return;
        }
        if (session.Event.WaitsForEntry)
        {
            logger.LogInformation("[{Session}] {Source}: next while the map loads, ignored", session.Id, session.Event.Source);
            return;
        }
        Advance(session);
    }

    /// <summary>Records the player's answer to the dialog that is open; the next conditions of the run read it.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="code">The answer code.</param>
    public void Answer(GameSession session, int code)
    {
        if (session.Event is not { OpenInteract: { } interact } run)
        {
            logger.LogInformation("[{Session}] dialog answer {Code} with no dialog open", session.Id, code);
            return;
        }
        run.Answer = (interact, code);
        run.OpenInteract = null;
        logger.LogInformation("[{Session}] {Source}: answer {Code} to dialog {Interact}", session.Id, run.Source, code, interact);

        if (run.MapId == EventArenas.LobbyMapId && EventArenas.ActivityOfAnswer(interact, code) is { } activity
            && !calendar.ActivityOpen(activity))
            RefuseActivity(session, run, (ushort)run.Index, activity);
    }

    /// <summary>Replaces the rest of a run with the NPC telling the player the activity is not open now, and when it is.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="run">The running event.</param>
    /// <param name="role">The NPC role that speaks.</param>
    /// <param name="activity">The activity name.</param>
    private void RefuseActivity(GameSession session, EventRun run, ushort role, string activity)
    {
        run.Groups.Clear();
        run.Pending.Clear();
        run.Pending.Enqueue(new EveScriptRecord(1, 2, role, 1, EventArenas.NotOpenTalk, 0, 0, 0, 1, 0)); // 2 Kind
        if (EventArenas.HoursTalk(activity) is not 0 and var hours)
            run.Pending.Enqueue(new EveScriptRecord(2, 2, role, 1, hours, 0, 0, 0, 1, 0)); // 2 Step, 2 Kind
        logger.LogInformation("[{Session}] {Source}: {Activity} is closed now (weekly hours)", session.Id, run.Source, activity);
    }

    /// <summary>Finds out whether a group warps to the arena of an activity that is closed now.</summary>
    /// <param name="scene">The scene of the map.</param>
    /// <param name="group">The condition group.</param>
    /// <returns>The activity name; null when the group does not lead to a closed activity.</returns>
    private string? ClosedActivity(EveScene scene, ConditionGroup group)
    {
        foreach (var r in group.Results)
        {
            if (ResultMapper.Map(r) is not { Action: ResultAction.Warp } mapped || scene.ConveyByIndex(mapped.Convey) is not { } convey)
                continue;
            var activity = EventArenas.ActivityOfMap(convey.TargetMapId);
            if (activity is not null && !calendar.ActivityOpen(activity))
                return activity;
        }
        return null;
    }

    /// <summary>Builds the function that gives the time the date conditions of a map's events compare with.</summary>
    /// <param name="mapId">The map id.</param>
    /// <returns>A function from the event index to the time.</returns>
    private Func<int, DateTime> ScriptClock(int mapId) => ev => calendar.ScriptNow(mapId, ev);

    /// <summary>Handles the end of a minigame: frees the client and stores the result for the conditions of the running event.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="result">The result the client reported; 0 means lost.</param>
    public void MinigameEnded(GameSession session, int result)
    {
        session.Send(EventMessages.SportFree());
        session.Minigames.Poke = null;
        session.Minigames.Wheel = null;
        if (session.Minigames.MallMinigame is { } mallGame)
        {
            session.Minigames.MallMinigame = null;
            logger.LogInformation("[{Session}] mall game {Kind} over", session.Id, mallGame);
            return;
        }
        if (session.Event is not { PendingMinigame: { } kind } run)
        {
            logger.LogInformation("[{Session}] minigame result {Result} with no minigame running", session.Id, result);
            return;
        }
        run.Rolls[kind] = result;
        run.PendingMinigame = null;
        logger.LogInformation("[{Session}] {Source}: minigame {Kind} {Outcome}", session.Id, run.Source, kind, result != 0 ? "won" : "lost");
    }

    /// <summary>
    /// Runs the results of the session's event one after the other, and the next condition groups that hold when they are used up, until one
    /// needs the client or the event ends.
    /// </summary>
    /// <param name="session">The player's session.</param>
    private void Advance(GameSession session)
    {
        var run = session.Event!;
        var character = session.ActiveCharacter!;
        while (true)
        {
            if (!run.Pending.TryDequeue(out var record))
            {
                if (NextGroup(session, run, character) || RestoreActedNpcs(session, run))
                    continue;
                break;
            }

            var mapped = ResultMapper.Map(record);
            switch (mapped.Action)
            {
                case ResultAction.Client when run.ServerStarted && mapped.Wire is { Kind: 0, Mode: 3, Option: 5 }: // NPC state change, sent if new (22/1)
                    var role = mapped.Wire.Target;
                    var value = (byte)mapped.Wire.Value;
                    if (!run.SentStates.TryGetValue(role, out var sent) || sent != value)
                    {
                        session.Send(EventMessages.NpcState(role, value));
                        run.SentStates[role] = value;
                        session.NpcStates[role] = value;
                        logger.LogInformation("[{Session}] {Source}: {What} (22/1)", session.Id, run.Source, mapped.Description);
                    }
                    break;

                case ResultAction.Client when run.ServerStarted && mapped.Wire is { Kind: 0, Mode: 3, Option: 4 }: // show an NPC to the player (22/11)
                    session.Send(EventMessages.NpcShowFor(mapped.Wire.Target, (ushort)Math.Clamp(mapped.Wire.Value, 0, ushort.MaxValue)));
                    session.NpcVisibility[mapped.Wire.Target] = (true, mapped.Wire.Value);
                    logger.LogInformation("[{Session}] {Source}: {What} (22/11)", session.Id, run.Source, mapped.Description);
                    break;

                case ResultAction.Client when run.ServerStarted && mapped.Wire is { Kind: 0, Mode: 3, Option: 3 }: // hide an NPC (22/3)
                    session.Send(EventMessages.NpcHide(mapped.Wire.Target, mapped.Wire.Value));
                    session.NpcVisibility[mapped.Wire.Target] = (false, mapped.Wire.Value);
                    logger.LogInformation("[{Session}] {Source}: {What} (22/3)", session.Id, run.Source, mapped.Description);
                    break;

                case ResultAction.Client: // any other client record: logs it, sends it and pauses the script
                    logger.LogInformation("[{Session}] {Source}: {What}{Text}", session.Id, run.Source, mapped.Description, TalkText(mapped.Wire));
                    if (mapped.Wire is { Kind: 0, Mode: 3, Option: 5 })
                        session.NpcStates[mapped.Wire.Target] = (byte)mapped.Wire.Value;
                    if (mapped.Wire is { Kind: 0, Mode: 3, Option: 3 })
                        run.ActedRoles.Remove(mapped.Wire.Target);
                    if (mapped.Wire is { Kind: 0, Mode: 3, Option: 3 or 4 })
                        session.NpcVisibility[mapped.Wire.Target] = (mapped.Wire.Option == 4, mapped.Wire.Value);
                    if (run.Instance is not null
                        && mapped.Wire is { Kind: 0, Mode: 3, Option: 3 or 4 }
                        && (mapped.Wire.Option == 4 || mapped.Wire.Value == ushort.MaxValue))
                        instanceHandlers.NpcHidden(session, mapped.Wire.Target, hidden: mapped.Wire.Option == 3,
                            shared: mapped.Wire.Option == 4 || run.GroupMovesInstance);
                    SendRecord(session, run, mapped.Wire);
                    if (mapped.Wire.Kind == 6)
                    {
                        run.OpenInteract = mapped.Wire.DataId;
                        run.Answer = null;
                    }
                    return;

                case ResultAction.Warp: // warp the player through a convey point of the map
                    var scene = maps.Scene(run.MapId);
                    var convey = scene?.ConveyByIndex(mapped.Convey);
                    if (convey is null)
                    {
                        logger.LogWarning("[{Session}] {Source}: convey {Convey} not found", session.Id, run.Source, mapped.Convey);
                        break;
                    }
                    var target = convey.TargetMapId == maps.SourceMapId(run.MapId) ? (ushort)run.MapId : convey.TargetMapId;
                    var playerId = session.Account!.LoginId;
                    if (!castles.MayPass(run.MapId, target, world.Teams.MembersOf(playerId).Append(playerId)))
                    {
                        logger.LogInformation("[{Session}] {Source}: past Trojan Round 1 only for the castle's guild", session.Id, run.Source);
                        run.Pending.Clear();
                        run.Groups.Clear();
                        SendRecord(session, run, new EventResult(1, 7, 0, 1, 0, 32036)); // 7 Mode, 32036 This transforming point is only for defenders.
                        return;
                    }
                    if (target == EventArenas.LobbyMapId && EventArenas.IsArena(run.MapId) && session.ActiveCharacter is { } leaver)
                        marks.SendRemoved(session, EventArenas.ClearRun(leaver, EventArenas.ProgressMarks(run.MapId)));
                    // a Carnie exit (Carnie's own door or the entrance hall's) takes the player back to where they came to Carnie from,
                    // when that place was kept
                    var carnieBack = session.ActiveCharacter is { } visitor ? CarnieReturn.Exit(visitor, run.MapId, target) : null;
                    world.ChangeMap(session, carnieBack is not null && maps.InstanceOfMap(carnieBack.MapId) is null ? carnieBack
                        : new DoorWarp(target, (ushort)Math.Clamp(convey.X, 0, ushort.MaxValue), (ushort)Math.Clamp(convey.Y, 0, ushort.MaxValue),
                            (ushort)convey.Index));
                    if (target == run.MapId)
                    {
                        // A move inside the map: the group's later results (show NPCs, marks, items) still belong to this map,
                        // so they go on once the client has loaded it again (EnterMap).
                        run.WaitsForEntry = true;
                        logger.LogInformation("[{Session}] {Source}: moved inside the map; the event goes on after the map loads", session.Id, run.Source);
                        return;
                    }
                    // Another map: the rest of this map's event no longer applies, except the group's quest marks, which
                    // the data sets after the warp for the new map's entry event to read (quest 244: the Inn's door 2
                    // warps to the Secret Room, then marks 14166, which shows Kurokawa there)
                    while (run.Pending.TryDequeue(out var rest))
                    {
                        var after = ResultMapper.Map(rest);
                        if (after.Action != ResultAction.Mark)
                            continue;
                        var done = ApplyMark(session, character, rest);
                        logger.LogInformation(
                            "[{Session}] {Source}: {What} (now {Count}) after the warp",
                            session.Id,
                            run.Source,
                            after.Description,
                            character.MarkCount(rest.SubKind));
                        if (done && marks.Stars(rest.SubKind) is var paidStars and > 0)
                        {
                            var star = ResultMapper.Map(new EveScriptRecord(rest.Step, 1, 1, 1, StarItem, 0, paidStars, 0, 0, 0));
                            var paid = ApplyItem(session, character, run, star with { Description = $"{star.Description} for finishing the quest" });
                            if (paid > 0)
                                SendRecord(session, run, star.Wire with { Value = paid });
                        }
                    }
                    run.Groups.Clear();
                    break;

                case ResultAction.Item: // give or take an item, telling the client when one was given
                    var given = ApplyItem(session, character, run, mapped);
                    if (given > 0)
                    {
                        SendRecord(session, run, mapped.Wire with { Value = given });
                        return;
                    }
                    break;

                case ResultAction.Gold: // change the player's gold and tell the client
                    var gold = (int)Math.Clamp((long)character.Gold + mapped.Wire.Value, 0, int.MaxValue);
                    var change = gold - character.Gold;
                    character.Gold = gold;
                    if (change != 0)
                        session.Send(Protocol.Messages.Player.PlayerMessages.MoneyChange(change));
                    logger.LogInformation("[{Session}] {Source}: {What} (now {Gold})", session.Id, run.Source, mapped.Description, gold);
                    break;

                case ResultAction.Service when mapped.Service == Career.ServiceCode: // open the career form
                    career.OpenForm(session);
                    break;

                case ResultAction.Service when mapped.Service == Services.RebornWish: // open the Reborn Wish
                    rebornWish.Open(session);
                    break;

                case ResultAction.Service when mapped.Service == Services.PointExchange: // open the point exchange
                    atm.OpenExchange(session);
                    break;

                case ResultAction.Service when mapped.Service == Services.NoticeBoard: // open the notice board
                    boards.OpenNoticeBoard(session);
                    break;

                case ResultAction.Service when mapped.Service == Services.Parking: // open the parking window
                    ParkingHandlers.Open(session, character);
                    break;

                case ResultAction.Service when mapped.Service == Services.VehicleRepair: // open the vehicle repair window
                    VehicleRepairHandlers.Open(session);
                    break;

                case ResultAction.Service when mapped.Service == Services.LostProperty: // open the lost property window
                    LostPropertyHandlers.Open(session);
                    break;

                case ResultAction.Service when mapped.Service == Services.Warehouse: // open the warehouse as the Item Bag
                    itemBag.Open(session, character);
                    break;

                case ResultAction.Service when mapped.Service == Services.Top100: // open the Top 100 list
                    boards.OpenTop100(session);
                    break;

                case ResultAction.PointStore when mapped.Service == 1: // add one row to the point store list
                    run.PointStoreRows.Add(new Protocol.Messages.Items.PointStoreRow((ushort)mapped.Convey, mapped.Wire.Value, mapped.Wire.Option));
                    break;

                case ResultAction.PointStore: // open the point store with the rows collected so far, then pause
                    mall.OpenPointStore(session, run.PointStoreRows);
                    session.Send(EventMessages.StopWaiting());
                    run.Started = true;
                    return;

                case ResultAction.Marriage: // run a marriage step: apply, wedding or divorce
                    logger.LogInformation("[{Session}] {Source}: {What}", session.Id, run.Source, mapped.Description);
                    marriage.Run(session, (MarriageStep)mapped.Service);
                    break;

                case ResultAction.Service: // open another service window, or skip it when not implemented
                    if (services.Open(session, mapped.Service))
                    {
                        session.Send(EventMessages.StopWaiting());
                        run.Started = true;
                        logger.LogInformation("[{Session}] {Source}: opened {What}", session.Id, run.Source, mapped.Description);
                        return;
                    }
                    logger.LogInformation("[{Session}] {Source}: skipped {What} (not implemented)", session.Id, run.Source, mapped.Description);
                    break;

                case ResultAction.Battle: // send the battle record and wait for its outcome
                    logger.LogInformation("[{Session}] {Source}: {What}", session.Id, run.Source, mapped.Description);
                    SendRecord(session, run, mapped.Wire);
                    run.PendingBattle = mapped.Service;
                    run.BattleOutcome = null;
                    return;

                case ResultAction.PlayerEmotion: // the player shows an emotion (32/1, emotion id 100 + n), shown to the others on the map
                {
                    var emotion = Protocol.Messages.World.WorldServerMessages.PlayerEmotion(session.Account!.LoginId, (byte)(100 + mapped.Service));
                    session.Send(emotion);
                    world.Broadcast(session, emotion, sameMapOnly: true);
                    logger.LogInformation("[{Session}] {Source}: {What}", session.Id, run.Source, mapped.Description);
                    break;
                }

                case ResultAction.RoleChange: // change the role
                    ChangeRole(session, character, run, (byte)mapped.Service);
                    break;

                case ResultAction.Packet: // send the prepared packet, if it has any bytes
                    if (mapped.Packet is { Length: > 0 })
                        session.Send(mapped.Packet);
                    logger.LogInformation("[{Session}] {Source}: {What}", session.Id, run.Source, mapped.Description);
                    break;

                case ResultAction.Minigame: // start a minigame and wait for it
                    session.Send(EventMessages.SportStart((byte)mapped.Service, (ushort)Math.Clamp(mapped.Wire.Value, 0, ushort.MaxValue), mapped.Wire.Option));
                    session.Send(EventMessages.StopWaiting());
                    run.PendingMinigame = mapped.Service;
                    run.Started = true;
                    logger.LogInformation("[{Session}] {Source}: {What}", session.Id, run.Source, mapped.Description);
                    return;

                case ResultAction.SavePoint: // record the player's return spot on this map
                    character.RecordMapId = (ushort)run.MapId;
                    character.RecordPoint = (ushort)mapped.Service;
                    character.RecordX = character.X;
                    character.RecordY = character.Y;
                    session.Send(new Protocol.IO.PacketWriter(Action5.Code, Action5.ReturnSpotRecorded).U8(1).ToArray());
                    logger.LogInformation("[{Session}] {Source}: {What} on map {Map}", session.Id, run.Source, mapped.Description, run.MapId);
                    break;

                case ResultAction.NpcAction: // an NPC plays an action; remember it acted
                    session.Send(mapped.Packet!);
                    if (!run.ServerStarted)
                        run.ActedRoles.Add((ushort)mapped.Service);
                    logger.LogInformation("[{Session}] {Source}: {What}", session.Id, run.Source, mapped.Description);
                    break;

                case ResultAction.Amity: // the amity of a team NPC changes
                    TeamNpcAmity(session, character, run, npcId: (ushort)mapped.Service, change: mapped.Convey);
                    break;

                case ResultAction.TeamNpc: // an NPC joins or leaves the team
                    TeamNpc(session, character, run, npcId: (ushort)mapped.Service, joins: mapped.Convey == 1);
                    break;

                case ResultAction.Hire: // charge the hire tier; a refusal talk is shown on click
                    run.HireTier = Hire(session, character, run, mapped.Service);
                    if (run.HireTier == 0 && run.Trigger == TriggerClick && HireTiers.Find(mapped.Service) is { RefusalTalk: > 0 } refused)
                    {
                        SendRecord(session, run, new EventResult(1, 3, (ushort)run.Index, 1, 0, (ushort)refused.RefusalTalk)); // 3 Mode
                        return;
                    }
                    break;

                case ResultAction.Exp: // give experience
                    experience.Gain(session, mapped.Service);
                    logger.LogInformation("[{Session}] {Source}: {What} (now {Exp})", session.Id, run.Source, mapped.Description, character.Exp);
                    break;

                case ResultAction.Gather when character.Skill(GatherPools.SkillFor(mapped.Service)) is null: // no skill: nothing here, and no cooldown starts
                    logger.LogInformation("[{Session}] {Source}: {What}: the skill is missing", session.Id, run.Source, mapped.Description);
                    run.Pending.Clear();
                    run.Groups.Clear();
                    SendRecord(session, run, new EventResult(1, 7, 0, 1, 0, NothingToCollectTalk)); // 7 Mode
                    return;

                case ResultAction.Gather: // the resource of a gathering spot, which also trains its skill
                    if (PickGather(session, run, mapped) is { } resource)
                    {
                        var collected = ApplyItem(session, character, run, resource);
                        if (collected > 0)
                        {
                            lifeSkills.Train(session, GatherPools.SkillFor(mapped.Service));
                            SendRecord(session, run, resource.Wire with { Value = collected });
                            return;
                        }
                    }
                    break;

                case ResultAction.LearnSkill: // teach the player a skill
                    LearnSkill(session, character, run, (ushort)mapped.Service);
                    break;

                case ResultAction.Cooldown: // start a script cooldown
                    character.StartCooldown((ushort)mapped.Service, GameClock.UtcNow, mapped.Convey);
                    logger.LogInformation("[{Session}] {Source}: {What}", session.Id, run.Source, mapped.Description);
                    break;

                case ResultAction.Mark: // change a quest mark; pay its stars when the quest is finished
                    var finished = ApplyMark(session, character, record);
                    logger.LogInformation(
                        "[{Session}] {Source}: {What} (now {Count})",
                        session.Id,
                        run.Source,
                        mapped.Description,
                        character.MarkCount(record.SubKind));
                    if (finished && marks.Stars(record.SubKind) is var stars and > 0)
                    {
                        var star = ResultMapper.Map(new EveScriptRecord(record.Step, 1, 1, 1, StarItem, 0, stars, 0, 0, 0));
                        var paid = ApplyItem(session, character, run, star with { Description = $"{star.Description} for finishing the quest" });
                        if (paid > 0)
                        {
                            SendRecord(session, run, star.Wire with { Value = paid });
                            return;
                        }
                    }
                    break;

                case ResultAction.InstanceGoal when run.Instance is { } instance: // add to an instance goal
                    instanceHandlers.AddGoal(session, instance, mapped.Service, mapped.Wire.Value);
                    break;

                case ResultAction.InstanceGoalDefined when run.Instance is { } instance: // define an instance goal
                    instanceHandlers.DefineGoal(session, instance, mapped.Service, (ushort)mapped.Convey, mapped.Wire.Value);
                    break;

                case ResultAction.InstanceGoal or ResultAction.InstanceGoalDefined: // instance goal outside an instance: skipped
                    logger.LogInformation("[{Session}] {Source}: skipped {What} (not in an instance)", session.Id, run.Source, mapped.Description);
                    break;

                default: // any other result: skipped, not implemented
                    logger.LogInformation("[{Session}] {Source}: skipped {What} (not implemented)", session.Id, run.Source, mapped.Description);
                    break;
            }
        }
        Finish(session, run);
    }

    /// <summary>Sends a result record to the client as the first or a following record of the run, after the server start packet for a lead run.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="run">The running event.</param>
    /// <param name="wire">The record to send.</param>
    private static void SendRecord(GameSession session, EventRun run, EventResult wire)
    {
        if (!run.Started && run.ServerStarted)
            session.Send(EventMessages.ServerStart());
        session.Send(EventMessages.Result(run.Started ? EventMessages.SubNext : EventMessages.SubFirst, wire));
        run.Started = true;
    }

    /// <summary>Takes the next condition group that holds and queues its results.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="run">The running event.</param>
    /// <param name="character">The character.</param>
    /// <returns>True when a group was taken, so the run goes on; false when no group is left.</returns>
    private bool NextGroup(GameSession session, EventRun run, Character character)
    {
        while (run.Groups.TryDequeue(out var group))
        {
            run.GuildId = guilds.Of(session.Account!.LoginId)?.Id ?? 0;
            if (run.GroupEvent is { } last && last != group.EventIndex)
                run.BattleOutcome = null; // the fight of an earlier event: this event's "battle won" groups wait for its own fight
            run.GroupEvent = group.EventIndex;
            if (!ConditionEvaluator.Holds(group, character, run, logger))
                continue;
            logger.LogInformation("[{Session}] {Source}: {Group} holds, {Count} result(s)", session.Id, run.Source, group.Label, group.Results.Count);
            if (group.Results.Count > 0)
                run.AnyGroupHeld = true;
            var scene = maps.Scene(run.MapId);
            if (run.MapId == EventArenas.LobbyMapId && scene is not null && ClosedActivity(scene, group) is { } closed)
            {
                var staff = scene.Npcs.ToList().FindIndex(n => n.NpcId == EventArenas.SignUpStaffNpcId);
                RefuseActivity(session, run, (ushort)Math.Max(1, staff + 1), closed);
                return true;
            }
            EmptyPickup(session, run, group);
            run.GroupMovesInstance = MovesInstance(group);
            foreach (var result in SelectResults(group.Results, run.ServerStarted ? scene?.LeadGroups : scene?.Groups))
                run.Pending.Enqueue(result);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Picks the results to run from those of a group: results that are not linked stay, and of the results of one link only one is chosen,
    /// by the weights the scene's group lists.
    /// </summary>
    /// <param name="results">The results of the group.</param>
    /// <param name="groups">The scene's link groups, or null for none.</param>
    /// <returns>The results to run, in order.</returns>
    private static List<EveScriptRecord> SelectResults(IReadOnlyList<EveScriptRecord> results, IReadOnlyList<EveGroup>? groups)
    {
        if (groups is null || results.All(r => r.Link == 0))
            return [.. results];
        var picked = new Dictionary<int, EveScriptRecord?>();
        List<EveScriptRecord> selected = [];
        foreach (var r in results)
        {
            var group = r.Link == 0 ? null : groups.FirstOrDefault(g => g.Index == r.Link);
            if (group is null || group.Unknown29 == 2)
            {
                selected.Add(r);
                continue;
            }
            if (!picked.TryGetValue(r.Link, out var choice))
            {
                var linked = results.Where(x => x.Link == r.Link).ToList();
                var weighted = linked.Select(x => (Result: x, Weight: (int)(group.Members.FirstOrDefault(m => m.Id == x.Step).Chance))).ToList();
                var total = weighted.Sum(w => w.Weight);
                if (total <= 0)
                    choice = linked[GameRandom.Shared.Next(linked.Count)];
                else
                {
                    var roll = GameRandom.Shared.Next(total);
                    choice = weighted.First(w => (roll -= w.Weight) < 0).Result;
                }
                picked[r.Link] = choice;
            }
            if (choice == r)
                selected.Add(r);
        }
        return selected;
    }

    /// <summary>Ends a run: clears it from the session, tells the client when it saw the run, and raises <see cref="EventFinished"/>.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="run">The run that ended.</param>
    private void Finish(GameSession session, EventRun run)
    {
        session.Event = null;
        if (!run.ServerStarted || run.Started)
            session.Send(EventMessages.Finished());
        EventFinished?.Invoke(session, run);
    }

    /// <summary>Called when a run has ended, with the session and the run.</summary>
    public Action<GameSession, EventRun>? EventFinished { get; set; }

    /// <summary>Looks up the dialog text of a talk record for the log.</summary>
    /// <param name="wire">The result record.</param>
    /// <returns>The text in quotes after a colon; empty when the record is not a talk record or the text is unknown.</returns>
    private string TalkText(EventResult wire) =>
        wire.Kind == 1 && talk.Table?[wire.DataId] is { } text ? $": \"{text}\"" : "";
}
