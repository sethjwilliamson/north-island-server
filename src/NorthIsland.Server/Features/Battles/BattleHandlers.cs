using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NorthIsland.Protocol.IO;
using NorthIsland.Protocol.Messages.Battle;
using NorthIsland.Protocol.Messages.Events;
using NorthIsland.Protocol.Messages.Items;
using NorthIsland.Protocol.Opcodes;

namespace NorthIsland.Server.Features.Battles;

/// <summary>The battle service: starts fights, runs rounds, and handles the battle packets and items.</summary>
/// <param name="maps">Map data for scenes, backgrounds and flags.</param>
/// <param name="npcs">NPC and monster records.</param>
/// <param name="skills">Skill records and motions.</param>
/// <param name="items">Item records.</param>
/// <param name="formulas">The game's formula tables.</param>
/// <param name="world">The shared world state: sessions, teams and hidden monsters.</param>
/// <param name="experience">The experience and level rules.</param>
/// <param name="combat">The source of the fighters' window values.</param>
/// <param name="instances">The instance handlers.</param>
/// <param name="calendar">The event calendar with the seasonal multipliers and drop tables.</param>
/// <param name="marks">The mark (quest flag) data.</param>
/// <param name="rankings">The rankings, which record PK wins.</param>
/// <param name="castles">The castle registry.</param>
/// <param name="options">The battle options.</param>
/// <param name="petOptions">The pet options.</param>
/// <param name="itemOptions">The item options.</param>
/// <param name="logger">The logger.</param>
public sealed partial class BattleHandlers(
    MapData maps,
    NpcData npcs,
    SkillData skills,
    ItemData items,
    FormulaData formulas,
    WorldState world,
    Experience experience,
    CombatStats combat,
    InstanceHandlers instances,
    EventCalendar calendar,
    MarkData marks,
    RankingRegistry rankings,
    CastleRegistry castles,
    IOptions<BattleOptions> options,
    IOptions<PetOptions> petOptions,
    IOptions<ItemOptions> itemOptions,
    ILogger<BattleHandlers> logger)
{
    /// <summary>The battle type sent for a fight against monsters.</summary>
    public const byte MonsterBattleType = 1;

    /// <summary>The seconds players have to choose their commands in a round.</summary>
    private const int CommandSeconds = 20;

    /// <summary>A join that came while a round was played and waits for the battle's next command phase.</summary>
    /// <param name="Target">The session of the player whose fight is joined.</param>
    /// <param name="Battle">The battle the target was in when the join came.</param>
    private sealed record WaitingJoin(GameSession Target, Battle Battle);

    /// <summary>
    /// The sessions whose join waits for the next command phase of the battle they asked to join; one wait each. <see cref="StartRound"/>
    /// joins them when the phase opens, <see cref="Close"/> answers them when the battle ends, and a session that leaves the world is dropped.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<GameSession, WaitingJoin> _waitingJoins = new();

    /// <summary>Hooks the battle service into the world: leaving players and players meeting on a map.</summary>
    internal void Start()
    {
        world.Leaving += OnLeavingWorld;
        world.PlayersMet += (a, b) =>
        {
            if (a.HiddenInBattle)
                b.Send(HiddenPacket(a));
            else if (a.Battle is { Phase: not BattlePhase.Over } ba)
                b.Send(FightStatePacket(a, ba.FightState));
            if (b.HiddenInBattle)
                a.Send(HiddenPacket(b));
            else if (b.Battle is { Phase: not BattlePhase.Over } bb)
                a.Send(FightStatePacket(b, bb.FightState));
        };
    }

    /// <summary>Handles a request to fight, watch or join a fight, and refuses it with a reason code when it cannot be played.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code: the kind, the target's id and, for a map NPC, its role.</param>
    internal ValueTask OnBattleRequest(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.State != SessionState.InWorld || session.ActiveCharacter is not { } character || body.Length < 5)
            return ValueTask.CompletedTask;
        var kind = body.Span[0];
        var targetId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(body.Span[1..]);
        if (kind == 3)
        {
            var role = body.Length >= 7 ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(body.Span[5..]) : (ushort)0;
            if (MapNpcRefusal(session, character, targetId, role) is { } why)
            {
                session.Send(new PacketWriter(Action33.Code, Action33.Option1).U8(8).ToArray()); // target busy
                logger.LogInformation("[{Session}] fight map NPC {Npc} (role {Role}): refused ({Why})", session.Id, targetId, role, why);
            }
            else
                StartNpcFight(session, npcs[(ushort)targetId]!, role);
            return ValueTask.CompletedTask;
        }
        GameSession? targetSession = world.SessionOf(targetId);
        var target = targetSession?.ActiveCharacter;
        byte? reason = kind switch
        {
            2 when target is { Option1: 2 } => 1, // 1 target hasn't turned on PK mode
            4 when target is { Option2: 2 } => 2, // 2 hasn't turned on battle view
            2 when target is not null && targetSession != session && target.MapId == character.MapId && !SameTeam(session, targetSession!)
                && IsFree(session) && IsFree(targetSession!) && !targetSession!.Encounters.Immune(GameClock.UtcNow) => null, // fight (kind 2): free, same map, another team
            4 when target is not null && targetSession!.Battle is { Phase: not BattlePhase.Over } && session.Battle is null
                && session.Watching is null && target.MapId == character.MapId => null, // watch (kind 4): target in a battle here
            5 when target is not null && targetSession != session && targetSession!.Battle is { Phase: not BattlePhase.Over }
                && IsFree(session) && target.MapId == character.MapId => null, // join (kind 5): target in a battle here
            _ => 8, // target busy
        };
        if (reason is { } refused)
        {
            session.Send(new PacketWriter(Action33.Code, Action33.Option1).U8(refused).ToArray());
            logger.LogInformation("[{Session}] battle request kind {Kind} on {Target}: refused ({Reason})", session.Id, kind, targetId, refused);
            return ValueTask.CompletedTask;
        }
        if (kind == 2)
            StartPk(session, targetSession!);
        else if (kind == 4)
            Watch(session, targetSession!.Battle!);
        else
            JoinFight(session, targetSession!);
        return ValueTask.CompletedTask;
    }

    /// <summary>The seconds a monster that started a fight stays hidden while it fights.</summary>
    private const int BattleHideSeconds = 3600;

    /// <summary>Tells whether a map NPC is a monster.</summary>
    /// <param name="mapId">The map.</param>
    /// <param name="role">The NPC's role (1-based index) on the map.</param>
    /// <returns>True when the NPC's record is a monster.</returns>
    private bool IsMonsterRole(ushort mapId, ushort role) =>
        maps.Scene(mapId) is { } scene && role >= 1 && role <= scene.Npcs.Count && npcs[(ushort)scene.Npcs[role - 1].NpcId] is { IsMonster: true };

    /// <summary>Tells whether a monster of the map started the battle.</summary>
    /// <param name="battle">The battle.</param>
    /// <returns>True when a map monster's event started it.</returns>
    private bool StartedByMonster(Battle battle) =>
        battle.StartedByMapNpcEvent && IsMonsterRole(battle.MapId, battle.SourceRole);

    /// <summary>Tells whether the monster that started the battle has a respawn of its own for each player.</summary>
    /// <param name="battle">The battle.</param>
    /// <returns>True for a roaming monster outside instances that counts toward no instance goal, while the setting is on.</returns>
    private bool HasPersonalRespawn(Battle battle)
    {
        if (!options.Value.PersonalRoamingMonsters || !StartedByMonster(battle) || maps.InstanceOfMap(battle.MapId) is not null)
            return false;
        var scene = maps.Scene(battle.MapId)!;
        var monster = scene.Npcs[battle.SourceRole - 1];
        return NpcWander.Roams(monster) && !scene.EventsOf(monster)
            .SelectMany(e => e.Conditions)
            .SelectMany(c => c.Results)
            .Any(r => ResultMapper.Map(r).Action == ResultAction.InstanceGoal); // the goal is the team's: one kill must count once
    }

    /// <summary>
    /// Hides the monster that started the battle: from the other players on the map, or only from the fighters when it has a respawn for each player.
    /// The fighters are not told while they fight.
    /// </summary>
    /// <param name="battle">The battle.</param>
    private void HideMonsterWhileFighting(Battle battle)
    {
        if (!StartedByMonster(battle))
            return;
        if (HasPersonalRespawn(battle))
        {
            foreach (var player in battle.Players)
                HideFromFighter(battle, player);
            logger.LogInformation("Monster {Role} of map {Map} is fighting: out of play for its fighters only", battle.SourceRole, battle.MapId);
            return;
        }
        world.HideMonster(battle.MapId, battle.SourceRole, BattleHideSeconds, except: s => ReferenceEquals(s.Battle, battle));
        logger.LogInformation("Monster {Role} of map {Map} is fighting: hidden for the others on the map", battle.SourceRole, battle.MapId);
    }

    /// <summary>Takes the monster that started the battle out of play for one of its fighters, without telling them while they fight.</summary>
    /// <param name="battle">The battle.</param>
    /// <param name="player">The fighter.</param>
    private void HideFromFighter(Battle battle, Fighter player) =>
        world.HideMonster(battle.MapId, battle.SourceRole, BattleHideSeconds, except: s => ReferenceEquals(s.Battle, battle), player: player.Id);

    /// <summary>
    /// Hides or shows again the monster that started a battle, once the battle is over: for everyone, or for each player who was in the fight,
    /// those who left it early too, when it has a respawn for each player.
    /// </summary>
    /// <param name="battle">The battle.</param>
    /// <param name="outcome">How the battle ended for the players.</param>
    /// <param name="fled">True when the players fled.</param>
    private void MonsterAfterBattle(Battle battle, BattleOutcome outcome, bool fled)
    {
        if (!StartedByMonster(battle))
            return;
        var seconds = outcome == BattleOutcome.Won ? options.Value.DefeatedMonsterHideSeconds : fled ? options.Value.FledMonsterHideSeconds : 0;
        if (!HasPersonalRespawn(battle))
        {
            if (seconds > 0)
                world.HideMonster(battle.MapId, battle.SourceRole, seconds);
            else
                world.ShowMonster(battle.MapId, battle.SourceRole);
            return;
        }
        foreach (var player in battle.Players.Select(p => p.Id).Distinct())
        {
            if (seconds > 0)
                world.HideMonster(battle.MapId, battle.SourceRole, seconds, player: player);
            else
                world.ShowMonster(battle.MapId, battle.SourceRole, player);
        }
    }

    /// <summary>Tells whether a player can start or join a fight.</summary>
    /// <param name="s">The player's session.</param>
    /// <returns>True when they are not in a battle, watching, in an event, in a stall, trading pets or fishing.</returns>
    public static bool IsFree(GameSession s) =>
        s.Battle is null && s.Watching is null && s.Event is null && !s.Stalls.Any && s.Trades.Pets is null
            && s.Activities.Fishing is null;

    /// <summary>Tells whether two players are in the same team.</summary>
    /// <param name="a">The session of one player.</param>
    /// <param name="b">The session of the other player.</param>
    /// <returns>True when both are in a team and it is the same one.</returns>
    private bool SameTeam(GameSession a, GameSession b) =>
        world.Teams.LeaderOf(a.Account!.LoginId) is var leader and not 0 && leader == world.Teams.LeaderOf(b.Account!.LoginId);

    /// <summary>Starts a fight between two players and plays its first round.</summary>
    /// <param name="attacker">The session of the player who challenges.</param>
    /// <param name="defender">The session of the player challenged; their team leader fights in their place when they are in a team.</param>
    /// <remarks>The fight is refused when the defender's team leader is the attacker, who would then fight on both sides.</remarks>
    private void StartPk(GameSession attacker, GameSession defender)
    {
        if (world.Teams.LeaderOf(defender.Account!.LoginId) is var leaderId and not 0 && leaderId != defender.Account.LoginId
            && world.SessionOf(leaderId) is { } leader && IsFree(leader))
            defender = leader;
        if (defender == attacker || SameTeam(attacker, defender))
        {
            attacker.Send(new PacketWriter(Action33.Code, Action33.Option1).U8(8).ToArray()); // target busy
            logger.LogWarning("[{Session}] PK on {Defender}: refused (the attacker would fight in the defending team)", attacker.Id,
                defender.ActiveCharacter?.Name);
            return;
        }
        var character = attacker.ActiveCharacter!;
        var battle = new Battle { EventBattleIndex = 0, IsPvp = true, Type = Action11.PkBattleType, Combat = combat };
        AddTeam(battle, attacker, side: 1);
        AddTeam(battle, defender, side: 2);
        battle.Background = maps.PkBackground(character.MapId, character.X, character.Y);
        Open(battle);
        logger.LogInformation("[{Session}] PK: {Attackers} against {Defenders}", attacker.Id,
            string.Join(", ", battle.Players.Where(p => p.Side == 1).Select(p => p.Name)),
            string.Join(", ", battle.Players.Where(p => p.Side == 2).Select(p => p.Name)));
        StartRound(attacker, battle);
    }

    /// <summary>Adds a team's leader and the members who are free and on the same map to a battle.</summary>
    /// <param name="battle">The battle.</param>
    /// <param name="leader">The team leader's session.</param>
    /// <param name="side">The side the team fights on.</param>
    private void AddTeam(Battle battle, GameSession leader, byte side)
    {
        var map = leader.ActiveCharacter!.MapId;
        battle.AddPlayer(leader.ActiveCharacter!, leader.Account!.LoginId, leader, Battle.PlayerCol, id => npcs[id], id => items[id], side);
        EnterBattle(leader, battle);
        var col = 0;
        foreach (var memberId in world.Teams.MembersOf(leader.Account.LoginId))
        {
            if (col >= Battle.MemberCols.Length)
                break;
            if (world.SessionOf(memberId) is not { } member || member.ActiveCharacter is not { } mc || mc.MapId != map || !IsFree(member))
                continue;
            battle.AddPlayer(mc, memberId, member, Battle.MemberCols[col++], id => npcs[id], id => items[id], side);
            EnterBattle(member, battle);
        }
    }

    /// <summary>
    /// Puts a player in a battle and closes the map-bound service windows open for them, so a bank, store or sell window opened before the fight is not
    /// served after it.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="battle">The battle they fight in.</param>
    private static void EnterBattle(GameSession session, Battle battle)
    {
        session.Battle = battle;
        OpenServices.CloseMapBound(session); // the item bag a Space Contract opened stays usable after the battle
    }

    /// <summary>Sends the fight start, battle type and weapon looks to the players, then tells the others on their map.</summary>
    /// <param name="battle">The battle.</param>
    private void Open(Battle battle)
    {
        foreach (var p in battle.Players)
        {
            p.Session!.Send(BattleMessages.FightStart(battle.Background, (byte)Math.Clamp(battle.EventBattleIndex, 0, 255),
                battle.FieldOrderFor(p).Select(f => f.ToRecord(own: ReferenceEquals(f, p), battle.FightState)), p.Session.Client));
            p.Session.Send(BattleMessages.BattleType(battle.Type));
            SendWeaponLooks(p.Session, battle);
        }
        foreach (var p in battle.Players)
            SendFightState(p.Session!, battle.FightState, skip: battle);
    }

    /// <summary>Tells the other players on the map that a player's fight state changed.</summary>
    /// <param name="s">The player whose state changed.</param>
    /// <param name="state">The new fight state.</param>
    /// <param name="skip">A battle whose players are not told, or null.</param>
    private void SendFightState(GameSession s, byte state, Battle? skip)
    {
        if (s.ActiveCharacter is not { } c)
            return;
        var packet = FightStatePacket(s, state);
        foreach (var other in world.OnMap(c.MapId).Where(o => o != s && (skip is null || o.Battle != skip)))
            other.Send(packet);
    }

    /// <summary>Builds the packet that removes a player from the others' scene by moving them to the hidden map.</summary>
    /// <param name="s">The player's session.</param>
    /// <returns>The packet.</returns>
    private static byte[] HiddenPacket(GameSession s) =>
        Protocol.Messages.World.WorldServerMessages.MapChange(s.Account!.LoginId, HiddenMapId, s.ActiveCharacter!.X, s.ActiveCharacter.Y, 0);

    /// <summary>Builds the packet that tells the client a player's fight state.</summary>
    /// <param name="s">The player's session.</param>
    /// <param name="state">The fight state.</param>
    /// <returns>The packet.</returns>
    private static byte[] FightStatePacket(GameSession s, byte state) =>
        new PacketWriter(Action11.Code, Action11.FightStates).U8(2).I32(s.Account!.LoginId).U16(0).U8(state).ToArray();

    /// <summary>Lets a player watch a battle.</summary>
    /// <param name="session">The watcher's session.</param>
    /// <param name="battle">The battle to watch.</param>
    private void Watch(GameSession session, Battle battle)
    {
        var id = session.Account!.LoginId;
        var own = new FighterRecord { State = Action11.WatchState, Kind = 2, Id = id, Row = 255, Col = 255 }; // player
        session.Send(BattleMessages.FightStart(battle.Background, (byte)Math.Clamp(battle.EventBattleIndex, 0, 255),
            [own, .. battle.FieldOrder().Where(f => !f.Left).Select(f => f.ToRecord(own: false))], session.Client));
        session.Send(BattleMessages.BattleType(battle.Type));
        SendWeaponLooks(session, battle);
        session.Watching = battle;
        lock (battle.Watchers)
            battle.Watchers.Add(session);
        logger.LogInformation("[{Session}] {Name} watches the battle of {Leader}", session.Id, session.ActiveCharacter!.Name, battle.Player.Name);
    }

    /// <summary>
    /// Lets a player join the fight another player is in, on that player's side, while its players choose their commands. A join that comes
    /// while a round is played (most of a battle's time) waits for the next command phase (<see cref="JoinWaitingAsync"/>) instead of being
    /// refused.
    /// </summary>
    /// <param name="session">The session of the joining player.</param>
    /// <param name="target">The session of the player whose fight is joined.</param>
    private void JoinFight(GameSession session, GameSession target)
    {
        var battle = target.Battle!;
        var targetFighter = battle.Players.Find(p => ReferenceEquals(p.Session, target) && !p.Left);
        byte[] cols = [Battle.PlayerCol, .. Battle.MemberCols];
        Fighter joined;
        Fighter? pet;
        lock (battle.Lock)
        {
            if (battle.Phase == BattlePhase.Acting && targetFighter is not null)
            {
                if (_waitingJoins.TryAdd(session, new WaitingJoin(target, battle)))
                {
                    logger.LogInformation("[{Session}] join the battle of {Target}: waits for the next command phase", session.Id,
                        target.ActiveCharacter?.Name);
                }
                else
                    logger.LogInformation("[{Session}] join the battle of {Target}: a join already waits", session.Id,
                        target.ActiveCharacter?.Name);
                return;
            }
            if (battle.Phase != BattlePhase.Command || targetFighter is null
                || cols.Where(c => !battle.Players.Exists(p => p.Side == targetFighter.Side && p.Col == c && !p.Left)).ToList() is not [var col, ..])
            {
                var why = targetFighter is null ? "the target left" : battle.Phase != BattlePhase.Command ? $"phase {battle.Phase}" : "no free place";
                var reason = targetFighter is null || battle.Phase != BattlePhase.Command ? (byte)4 : (byte)1; // 4 the battle is over, 1 no place
                session.Send(new PacketWriter(Action11.Code, Action11.JoinBattle).U8(reason).ToArray());
                logger.LogInformation("[{Session}] join the battle of {Target}: refused ({Why})", session.Id, target.ActiveCharacter?.Name, why);
                return;
            }
            joined = battle.AddPlayer(session.ActiveCharacter!, session.Account!.LoginId, session, col, id => npcs[id], id => items[id], targetFighter.Side);
            joined.Joined = true;
            joined.JoinedRound = battle.Round; // the joiner acts from the next round
            pet = battle.PetOf(joined);
            pet?.JoinedRound = battle.Round;
            EnterBattle(session, battle);
            if (HasPersonalRespawn(battle))
                HideFromFighter(battle, joined); // under the lock, so the end of the battle cannot come first
        }
        Fighter[] newcomers = pet is null ? [joined] : [joined, pet];
        battle.Send(client => BattleMessages.Join(newcomers.Select(f => f.ToRecord(own: false)), client), except: session);
        session.Send(BattleMessages.FightStart(battle.Background, (byte)Math.Clamp(battle.EventBattleIndex, 0, 255),
            battle.FieldOrderFor(joined).Where(f => !f.Left).Select(f => f.ToRecord(own: ReferenceEquals(f, joined), 2)), session.Client));
        session.Send(BattleMessages.BattleType(battle.Type));
        SendWeaponLooks(session, battle);
        HideJoiner(session, battle);
        logger.LogInformation("[{Session}] {Name} joins the battle of {Target} at ({Row},{Col})", session.Id, joined.Name,
            target.ActiveCharacter?.Name, joined.Row, joined.Col);
    }

    /// <summary>
    /// Joins the players whose join waits for the command phase that has just opened in a battle. Each join runs on its own task under the
    /// joiner's session gate, so the caller, which may hold another session's gate, never waits on it.
    /// </summary>
    /// <param name="battle">The battle whose command phase opened.</param>
    private void JoinWaiting(Battle battle)
    {
        foreach (var (session, waiting) in _waitingJoins)
        {
            if (ReferenceEquals(waiting.Battle, battle) && _waitingJoins.TryRemove(new KeyValuePair<GameSession, WaitingJoin>(session, waiting)))
                _ = Task.Run(() => JoinWaitingAsync(session, waiting.Target, battle));
        }
    }

    /// <summary>Tells the players whose join waits for a battle that it is over, and drops their joins.</summary>
    /// <param name="battle">The battle that ended.</param>
    private void DropWaitingJoins(Battle battle)
    {
        foreach (var (session, waiting) in _waitingJoins)
        {
            if (!ReferenceEquals(waiting.Battle, battle) || !_waitingJoins.TryRemove(new KeyValuePair<GameSession, WaitingJoin>(session, waiting)))
                continue;
            session.Send(new PacketWriter(Action11.Code, Action11.JoinBattle).U8(4).ToArray()); // 4: the battle is over
            logger.LogInformation("[{Session}] join the battle of {Target} after waiting: refused (the battle is over)", session.Id,
                waiting.Target.ActiveCharacter?.Name);
        }
    }

    /// <summary>
    /// Joins a battle a player asked to join while a round was played, now that its command phase opened, under the joiner's session gate: if
    /// the joiner is still connected, the live session of their account, free and on the map of the player they join, and that player still
    /// fights in the battle. A battle that ends or leaves the target meanwhile is answered as over; any other change drops the join.
    /// </summary>
    /// <param name="session">The session of the joining player.</param>
    /// <param name="target">The session of the player whose fight is joined.</param>
    /// <param name="battle">The battle the target was in when the join came.</param>
    /// <returns>A task that completes when the join is done or dropped.</returns>
    private async Task JoinWaitingAsync(GameSession session, GameSession target, Battle battle)
    {
        try
        {
            using var exclusive = await session.ExclusiveAsync().ConfigureAwait(false); // the join changes the joiner's state
            var over = battle.Phase == BattlePhase.Over || !ReferenceEquals(target.Battle, battle);
            var why = over ? "the battle is over"
                : session.Closing || session.State != SessionState.InWorld || session.LeavingGame || session.Account is not { } account
                    || !ReferenceEquals(world.SessionOf(account.LoginId), session) || session.ActiveCharacter is not { } c ? "left the world"
                    : !IsFree(session) ? "busy"
                        : target.ActiveCharacter?.MapId != c.MapId ? "no longer on the map of the battle"
                            : null;
            if (why is null)
            {
                JoinFight(session, target); // waits again when the round has begun to play meanwhile
                return;
            }
            logger.LogInformation("[{Session}] join the battle of {Target} after waiting: refused ({Why})", session.Id,
                target.ActiveCharacter?.Name, why);
            if (over)
                session.Send(new PacketWriter(Action11.Code, Action11.JoinBattle).U8(4).ToArray()); // 4: the battle is over
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[{Session}] join the battle of {Target} after waiting failed", session.Id, target.ActiveCharacter?.Name);
        }
    }

    /// <summary>Hides a joining player from the other players on their map.</summary>
    /// <param name="session">The joiner's session.</param>
    /// <param name="battle">The battle they joined.</param>
    private void HideJoiner(GameSession session, Battle battle)
    {
        if (session.ActiveCharacter is not { } c)
            return;
        session.HiddenInBattle = true;
        var packet = HiddenPacket(session);
        foreach (var other in world.OnMap(c.MapId).Where(o => o != session && !ReferenceEquals(o.Battle, battle)))
            other.Send(packet);
    }

    /// <summary>The map id (0) used to make a fighting player disappear from the others' scene.</summary>
    public const ushort HiddenMapId = 0;

    /// <summary>Shows a hidden joiner to the other players on their map again.</summary>
    /// <param name="session">The joiner's session.</param>
    /// <param name="battle">The battle they were in.</param>
    private void ShowJoiner(GameSession session, Battle battle)
    {
        if (!session.HiddenInBattle)
            return;
        session.HiddenInBattle = false;
        if (session.LeavingGame || session.ActiveCharacter is not { } c)
            return;
        var packet = Protocol.Messages.World.WorldServerMessages.MapChange(session.Account!.LoginId, c.MapId, c.X, c.Y, 0);
        foreach (var other in world.OnMap(c.MapId).Where(o => o != session && !ReferenceEquals(o.Battle, battle)))
            other.Send(packet);
        world.ShowPet(session, except: o => ReferenceEquals(o.Battle, battle));
    }

    /// <summary>Sends the looks of the weapons the pets in the battle wield.</summary>
    /// <param name="session">The session that receives the packet.</param>
    /// <param name="battle">The battle.</param>
    private static void SendWeaponLooks(GameSession session, Battle battle)
    {
        var looks = battle.Pets.Where(f => !f.Left && f.OwnedPet?.Equipment.Find(e => e.Slot == Gems.WeaponSlot) is not null)
            .Select(f => (f.Id, f.OwnerId, f.Row, f.Col, f.OwnedPet!.Equipment.Find(e => e.Slot == Gems.WeaponSlot)!.ItemId)).ToList();
        if (looks.Count > 0)
            session.Send(BattleMessages.EquipLook(looks));
    }

    /// <summary>Removes a watcher from a battle and tells their client.</summary>
    /// <param name="session">The watcher's session.</param>
    /// <param name="battle">The battle they watched.</param>
    private static void StopWatching(GameSession session, Battle battle)
    {
        lock (battle.Watchers)
            battle.Watchers.Remove(session);
        session.Watching = null;
        session.Send(new PacketWriter(Action11.Code, Action11.PlayerLeft).I32(session.Account!.LoginId).U16(0).ToArray());
    }

    /// <summary>
    /// Ends a fight between players: records the winners' PK wins and ranks, then ends the battle; nothing happens when the battle is
    /// already over, so the wins are recorded once.
    /// </summary>
    /// <param name="session">The session the end is reported for.</param>
    /// <param name="battle">The battle.</param>
    /// <param name="winner">The winning side.</param>
    /// <param name="holder">The session whose gate the caller holds, or null when it holds none or cannot tell (the round's play).</param>
    private void EndPvp(GameSession session, Battle battle, byte winner, GameSession? holder = null)
    {
        if (!TryClaimEnd(battle))
            return;
        var winners = string.Join(", ", battle.Players.Where(p => p.Side == winner).Select(p => p.Name));
        foreach (var p in battle.Players.Where(p => p.Side == winner))
        {
            rankings.AddPkWin(p.Id, p.Name);
            if (p.Session is not { } winnerSession)
                continue;
            winnerSession.HandOver(holder, "PK win", () =>
            {
                if (winnerSession.ActiveCharacter is not { } winnerCharacter)
                    return;
                winnerCharacter.PvpWins = (ushort)Math.Min(ushort.MaxValue, winnerCharacter.PvpWins + 1);
                var wins = new PacketWriter(Action37.Code, Action37.PvpVictories).I32(p.Id).U16(winnerCharacter.PvpWins).ToArray();
                winnerSession.Send(wins);
                world.Broadcast(winnerSession, wins, sameMapOnly: false);
            });
        }
        UpdatePvpRanks();
        Close(session, battle, BattleOutcome.Won, $"PK won by {winners}", holder: holder);
    }

    /// <summary>Gives the top three PK winners rank 1 to 3 and tells everyone online whose rank changed.</summary>
    private void UpdatePvpRanks()
    {
        var top = rankings.PkTop().Take(3).Select(r => r.PlayerId).ToList();
        foreach (var other in world.OnlineSessions().ToList())
        {
            if (other.ActiveCharacter is not { } c || other.Account is null)
                continue;
            var rank = (byte)(top.IndexOf(other.Account.LoginId) + 1);
            if (c.PvpRank == rank)
                continue;
            c.PvpRank = rank;
            var packet = new PacketWriter(Action37.Code, Action37.PvpRank).I32(other.Account.LoginId).U8(rank).ToArray();
            other.Send(packet);
            world.Broadcast(other, packet, sameMapOnly: false);
        }
    }

    /// <summary>Tells why a player may not fight a map NPC.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="character">The player's character.</param>
    /// <param name="npcId">The NPC record id the client sent.</param>
    /// <param name="role">The NPC's role (1-based index) on the player's map.</param>
    /// <returns>The reason, or null when the fight is allowed.</returns>
    private string? MapNpcRefusal(GameSession session, Data.Character character, int npcId, ushort role)
    {
        if (maps.Scene(character.MapId) is not { } scene || role < 1 || role > scene.Npcs.Count)
            return "no such role here";
        if (scene.Npcs[role - 1].NpcId != npcId || npcs[(ushort)npcId] is not { } npc)
            return $"role {role} is not npc {npcId}";
        if (npc.NoPk)
            return "PK is prohibited (Npc.dat @110)";
        if (npcs.IsSceneObject(npc.Id))
            return "a scene object";
        if ((maps.SceneFlags(character.MapId) & 1) != 0)
            return "no PK on this map";
        if (world.MonsterIsOut(character.MapId, role) || world.MonsterIsOutFor(character.MapId, role, session.Account!.LoginId)
            || session.HiddenNpcs.TryGetValue((character.MapId, role), out var until) && until > GameClock.UtcNow)
            return "hidden";
        if (!IsFree(session))
            return "busy";
        return FighterOfNpc(character.MapId, role) is not null ? "already fighting" : null;
    }

    /// <summary>Starts a fight between a player (with their team) and a map NPC and plays its first round.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="npc">The NPC's record.</param>
    /// <param name="role">The NPC's role (1-based index) on the map.</param>
    private void StartNpcFight(GameSession session, NorthIsland.GameData.Npcs.NpcRecord npc, ushort role)
    {
        var character = session.ActiveCharacter!;
        var battle = Battle.CreateAgainstMapNpc(character, session.Account!.LoginId, npc, role, session, id => npcs[id], id => items[id], combat);
        var col = 0;
        foreach (var memberId in world.Teams.MembersOf(session.Account.LoginId))
        {
            if (col >= Battle.MemberCols.Length)
                break;
            if (world.SessionOf(memberId) is not { } member || member.ActiveCharacter is not { } mc || mc.MapId != character.MapId || !IsFree(member))
                continue;
            battle.AddPlayer(mc, memberId, member, Battle.MemberCols[col++], id => npcs[id], id => items[id]);
            EnterBattle(member, battle);
        }
        EnterBattle(session, battle);
        battle.Background = maps.PkBackground(character.MapId, character.X, character.Y);
        battle.Type = MonsterBattleType;
        Open(battle);
        logger.LogInformation("[{Session}] {Names} fight map NPC {Npc} ({Id}, role {Role}, lv {Level}, {Hp} HP) on map {Map}", session.Id,
            string.Join(", ", battle.Players.Select(p => p.Name)), npc.Name, npc.Id, role, npc.Level, battle.Monsters[0].Hp, character.MapId);
        StartRound(session, battle);
    }

    /// <summary>Finds the player who is fighting a map NPC on their own.</summary>
    /// <param name="mapId">The map.</param>
    /// <param name="role">The NPC's role (1-based index) on the map.</param>
    /// <returns>The session of the fight's first player, or null when nobody is fighting it.</returns>
    private GameSession? FighterOfNpc(ushort mapId, ushort role) =>
        world.OnMap(mapId).FirstOrDefault(s => s.Battle is { Standalone: true, Phase: not BattlePhase.Over } b && b.SourceRole == role
            && ReferenceEquals(b.Player.Session, s));

    /// <summary>Handles the use of the Escape Button item in a battle: the player leaves with their team.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code: the bag slot of the item.</param>
    internal ValueTask OnEscapeButton(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.State != SessionState.InWorld
            || session.ActiveCharacter is not { } character
            || body.Length < 1
            || session.Battle is not { Phase: not BattlePhase.Over } battle)
            return ValueTask.CompletedTask;
        var stack = character.Bag.Find(b => b.Slot == body.Span[0]);
        var player = battle.ActivePlayers.FirstOrDefault(p => ReferenceEquals(p.Session, session));
        if (stack is null || stack.ItemId != EscapeButton || items[EscapeButton] is not { } item || player is null)
        {
            logger.LogInformation("[{Session}] {Name}: escape button refused (slot {Slot})", session.Id, character.Name, body.Span[0]); // Slot
            return ValueTask.CompletedTask;
        }
        UseLimit.Spend(session, character, stack, item);
        LeaveWithTeam(session, battle, player, "escape button", holder: session);
        return ValueTask.CompletedTask;
    }

    /// <summary>The item ids of the Escape Button and the Win Plate.</summary>
    public const ushort EscapeButton = 34164, WinPlate = 34165; // 34164 Escape Button, 34165 Win Plate

    /// <summary>Handles the use of the Win Plate item: the player wins the battle at once, outside PK, NPC fights and castle wars.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code: the bag slot of the item is its second byte.</param>
    internal ValueTask OnWinPlate(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.State != SessionState.InWorld
            || session.ActiveCharacter is not { } character
            || body.Length < 2
            || session.Battle is not { Phase: not BattlePhase.Over } battle)
            return ValueTask.CompletedTask;
        var stack = character.Bag.Find(b => b.Slot == body.Span[1]);
        if (stack is null || stack.ItemId != WinPlate || battle.IsPvp || battle.Standalone || (castles.WarOn && CastleRegistry.IsCastleMap(character.MapId))
            || battle.ActivePlayers.All(p => !ReferenceEquals(p.Session, session)))
        {
            logger.LogInformation("[{Session}] {Name}: win plate refused (slot {Slot})", session.Id, character.Name, body.Span[1]); // Slot
            return ValueTask.CompletedTask;
        }
        stack.Count--;
        session.Send(ItemMessages.BagSlotDecrease(stack.Slot, 1));
        if (stack.Count == 0)
            character.Bag.Remove(stack);
        logger.LogInformation("[{Session}] {Name} wins the battle with a Win Plate", session.Id, character.Name);
        Win(session, battle, winPlate: battle.ActivePlayers.FirstOrDefault(p => ReferenceEquals(p.Session, session)), holder: session);
        return ValueTask.CompletedTask;
    }

    /// <summary>The item id of the Delay Clock.</summary>
    public const ushort DelayClock = 34166; // Delay Clock

    /// <summary>Handles the use of the Delay Clock item: the battle's command timer starts again.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code: the bag slot of the item.</param>
    internal ValueTask OnDelayClock(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.State != SessionState.InWorld
            || session.ActiveCharacter is not { } character
            || body.Length < 1
            || session.Battle is not { Phase: not BattlePhase.Over } battle)
            return ValueTask.CompletedTask;
        var stack = character.Bag.Find(b => b.Slot == body.Span[0]);
        if (stack is null || stack.ItemId != DelayClock || battle.IsPvp || battle.DelayClockUsed || battle.Type is 3 or 4 or 5 or 8 or 9 or 11
            || battle.ActivePlayers.All(p => !ReferenceEquals(p.Session, session)))
        {
            logger.LogInformation("[{Session}] {Name}: delay clock refused (slot {Slot})", session.Id, character.Name, body.Span[0]); // Slot
            return ValueTask.CompletedTask;
        }
        battle.DelayClockUsed = true;
        stack.Count--;
        session.Send(ItemMessages.BagSlotDecrease(stack.Slot, 1));
        if (stack.Count == 0)
            character.Bag.Remove(stack);
        battle.Send(_ => new PacketWriter(Action51.Code, Action51.BattleTimeReset).ToArray());
        logger.LogInformation("[{Session}] {Name} uses a Delay Clock", session.Id, character.Name);
        return ValueTask.CompletedTask;
    }

    /// <summary>Handles the join cursor on a map NPC that is fighting: the player joins that fight, or is told the battle is over.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code: the NPC id and its role.</param>
    internal ValueTask OnJoinBattle(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.State != SessionState.InWorld || session.ActiveCharacter is not { } character)
            return ValueTask.CompletedTask;
        var role = body.Length >= 6 ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(body.Span[4..]) : (ushort)0;
        if (IsFree(session) && FighterOfNpc(character.MapId, role) is { } fighter)
            JoinFight(session, fighter);
        else
            session.Send(new PacketWriter(Action11.Code, Action11.JoinBattle).U8(4).ToArray());
        return ValueTask.CompletedTask;
    }

    /// <summary>Handles the client's start of a battle that an event is waiting for, or a watcher's request to stop watching.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code (not read).</param>
    internal ValueTask OnStartEventBattle(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.Watching is { } watched)
        {
            StopWatching(session, watched);
            return ValueTask.CompletedTask;
        }
        var character = session.ActiveCharacter;
        if (session.State != SessionState.InWorld || character is null || session.Battle is not null)
            return ValueTask.CompletedTask;
        if (session.Event is not { PendingBattle: { } index } run)
        {
            logger.LogWarning("[{Session}] 11/1 without a battle waiting", session.Id);
            return ValueTask.CompletedTask;
        }
        run.PendingBattle = null;

        // only these triggers carry an NPC's role: the index of an area run is its encounter zone's
        var sourceRole = run.Trigger is EventEngine.TriggerClick or EventEngine.TriggerNear ? (ushort)run.Index : (ushort)0;
        var eventMap = (ushort)run.MapId; // the battle is one of the map whose event is waiting for it, wherever the player stands now
        var source = maps.Scene(eventMap)?.Battles.FirstOrDefault(b => b.Index == index);
        var battle = source is null
            ? null
            : Battle.Create(character, session.Account!.LoginId, source, eventMap, id => npcs[id], sourceRole, session, id => items[id], combat);
        if (battle is null || battle.Monsters.Count == 0)
        {
            logger.LogWarning("[{Session}] battle {Index} of map {Map} has no monsters", session.Id, index, eventMap);
            run.BattleOutcome = BattleOutcome.Lost;
            session.Send(EventMessages.StopWaiting());
            return ValueTask.CompletedTask;
        }

        var col = 0;
        foreach (var memberId in world.Teams.MembersOf(session.Account!.LoginId))
        {
            if (col >= Battle.MemberCols.Length)
                break;
            if (world.SessionOf(memberId) is not { } member || member.ActiveCharacter is not { } mc || mc.MapId != character.MapId || !IsFree(member))
                continue;
            // a member waiting for their own respawn stays out, but for the next of several battles in a row of the same event
            if (sourceRole != 0 && world.MonsterIsOutFor(eventMap, sourceRole, memberId) && !run.Fighters.Contains(memberId))
                continue;
            battle.AddPlayer(mc, memberId, member, Battle.MemberCols[col++], id => npcs[id], id => items[id]);
            EnterBattle(member, battle);
            run.Fighters.Add(memberId);
        }

        EnterBattle(session, battle);
        battle.Background = maps.BattleBackground(character.MapId, character.X, character.Y);
        battle.Type = MonsterBattleType;
        Open(battle);
        HideMonsterWhileFighting(battle);
        session.Send(EventMessages.StopWaiting());
        logger.LogInformation("[{Session}] {Names} fight battle {Index} of map {Map}: {Monsters}", session.Id,
            string.Join(", ", battle.Players.Select(p => p.Name)), index, eventMap,
            string.Join(", ", battle.Monsters.Select(m => $"{m.Name} (lv {m.Level}, {m.Hp} HP) at ({m.Row},{m.Col})")));
        StartRound(session, battle);
        return ValueTask.CompletedTask;
    }
}