using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NorthIsland.GameData.Maps;
using NorthIsland.Protocol.IO;
using NorthIsland.Protocol.Messages.Events;
using Codes = NorthIsland.Protocol.Messages.Events.InstanceInitCodes;
using InstanceJoinCodes = NorthIsland.Protocol.Messages.Events.InstanceJoinCodes;

namespace NorthIsland.Server.Features.Instances;

/// <summary>
/// Handles the instance window and the running instances: listing, creating, joining, leaving, entering and kicking, the goals and
/// hidden npcs, and the timer that starts waiting instances and closes finished ones.
/// </summary>
/// <param name="world">The world state, used to find sessions and to move players between maps.</param>
/// <param name="maps">The map data, which holds the instance scenes.</param>
/// <param name="instances">The registry of the live instances.</param>
/// <param name="marks">The mark data, used for the done-today flags.</param>
/// <param name="guilds">The guild registry, used for guild-only instances.</param>
/// <param name="travel">The teleport rule, for the maps and states that keep a player from entering the instance.</param>
/// <param name="options">The instance options.</param>
/// <param name="serverOptions">The server options; the spawn point is where players are sent out to.</param>
/// <param name="logger">The logger.</param>
public sealed class InstanceHandlers(
    WorldState world,
    MapData maps,
    InstanceRegistry instances,
    MarkData marks,
    GuildRegistry guilds,
    TravelRules travel,
    IOptions<InstanceOptions> options,
    IOptions<ServerOptions> serverOptions,
    ILogger<InstanceHandlers> logger) : IDisposable
{
    /// <summary>The number of instances shown on one page of the list.</summary>
    public const int RowsPerPage = 5;

    /// <summary>The number of members shown on one page of the member list.</summary>
    public const int MembersPerPage = 5;

    /// <summary>The timer that calls <c>Tick</c> every five seconds.</summary>
    private Timer? _timer;

    /// <summary>Subscribes to the arrival of players on a map and starts the timer.</summary>
    internal void Start()
    {
        world.Arrived += Arrived;
        world.MapChanging += RememberEntry;
        _timer = new Timer(_ => Tick(GameClock.UtcNow), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    /// <summary>Handles asking for a page of the list (<c>85/2</c>).</summary>
    /// <param name="s">The player's session.</param>
    /// <param name="b">The packet body after the action and sub code; its first byte is the page.</param>
    internal ValueTask OnListPage(GameSession s, ReadOnlyMemory<byte> b) => Handle(s, () => SendList(s, b.Length > 0 ? b.Span[0] : (byte)1));

    /// <summary>Handles asking for a page of the member list (<c>85/11</c>).</summary>
    /// <param name="s">The player's session.</param>
    /// <param name="b">The packet body after the action and sub code; its first byte is the page.</param>
    internal ValueTask OnMemberPage(GameSession s, ReadOnlyMemory<byte> b) => Handle(s, () => SendMembers(s, b.Length > 0 ? b.Span[0] : (byte)1));

    public void Dispose() => _timer?.Dispose();

    /// <summary>Runs an instance action for a session that is in the world with a character and an account; ignores any other session.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="action">The action to run.</param>
    internal static ValueTask Handle(GameSession session, Action action)
    {
        if (session.State == SessionState.InWorld && session.ActiveCharacter is not null && session.Account is not null)
            action();
        return ValueTask.CompletedTask;
    }

    /// <summary>Returns the login id of the session's account.</summary>
    /// <param name="session">The player's session.</param>
    /// <returns>The login id.</returns>
    private static int Id(GameSession session) => session.Account!.LoginId;

    /// <summary>
    /// Starts a new server day for the character's instances: once per day it clears the done-today marks and the titles of every instance.
    /// </summary>
    /// <param name="session">The player's session; null when the player is not online, as at login, so no packets are sent.</param>
    /// <param name="character">The character.</param>
    public void ResetDay(GameSession? session, Character character)
    {
        var today = GameClock.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (character.InstanceDay == today)
            return;
        character.InstanceDay = today;
        foreach (var scene in maps.Instances)
        {
            if (session is not null)
                marks.TrySetFlag(session, character, scene.DoneMark, false);
            else
                character.MarkFlags.Remove(scene.DoneMark);
            if (character.Marks.RemoveAll(m => m.Id == scene.TitleMark || m.Id == scene.DoneMark) > 0 && session is not null)
                session.Send(MarkMessages.Delete(scene.TitleMark));
        }
    }

    /// <summary>Returns whether the character is below the level the scene needs, when the level check is switched on.</summary>
    /// <param name="character">The character.</param>
    /// <param name="scene">The instance scene.</param>
    /// <returns>True when the character is too low.</returns>
    private bool TooLow(Character character, InstanceScene scene) => options.Value.InstanceLevelCheck && character.Level < scene.MinLevel;

    /// <summary>Returns whether the character has already done the instance today.</summary>
    /// <param name="character">The character.</param>
    /// <param name="scene">The instance scene.</param>
    /// <returns>True when the done mark of the scene is set.</returns>
    public static bool DoneToday(Character character, InstanceScene scene) => character.MarkCount(scene.DoneMark) > 0;

    /// <summary>
    /// Fixes the saved position of a character who logs in: one saved on an instance map is sent back to its return point unless the player
    /// is still a member of that live instance.
    /// </summary>
    /// <param name="character">The character logging in.</param>
    /// <param name="playerId">The player's login id.</param>
    public void PlaceAtLogin(Character character, int playerId)
    {
        ResetDay(null, character);
        if (maps.InstanceOfMap(character.MapId) is null)
            return;
        var instance = instances.ByMap(character.MapId);
        if (instance is not null)
        {
            lock (instance.Lock)
            {
                if (instance.Member(playerId) is not null)
                    return;
            }
        }
        var back = ReturnPoint(character);
        (character.MapId, character.X, character.Y) = (back.MapId, back.X, back.Y);
    }

    /// <summary>Sends the player who logs in the state of the instance they are a member of, if any.</summary>
    /// <param name="session">The player's session.</param>
    public void SendOnLogin(GameSession session)
    {
        if (instances.Of(Id(session)) is not { } instance)
            return;
        SendState(session, instance);
        Arrived(session);
    }

    /// <summary>Returns where a character is sent when it leaves an instance: the place it entered from, or else the server spawn point.</summary>
    /// <param name="character">The character.</param>
    /// <returns>The door warp to that place.</returns>
    private DoorWarp ReturnPoint(Character character)
    {
        if (character.InstanceReturnMapId != 0 && maps.InstanceOfMap(character.InstanceReturnMapId) is null)
            return new DoorWarp(character.InstanceReturnMapId, character.InstanceReturnX, character.InstanceReturnY, 0);
        var spawn = serverOptions.Value.Spawn;
        return new DoorWarp(spawn.MapId, spawn.X, spawn.Y, 0);
    }

    /// <summary>Opens the instance window for a player: starts their day, sends the first page of the list and the state of their own instance.</summary>
    /// <param name="session">The player's session.</param>
    internal void OpenList(GameSession session)
    {
        ResetDay(session, session.ActiveCharacter!);
        SendList(session, 1);
        if (instances.Of(Id(session)) is { } instance)
            SendState(session, instance);
    }

    /// <summary>Sends a page of the waiting instances; the page number is kept inside the available pages.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="page">The wanted page, 1-based.</param>
    private void SendList(GameSession session, byte page)
    {
        var open = instances.Waiting();
        var pages = Math.Max(1, (open.Count + RowsPerPage - 1) / RowsPerPage);
        page = (byte)Math.Clamp((int)page, 1, pages);
        List<InstanceRow> rows = [];
        foreach (var i in open.Skip((page - 1) * RowsPerPage).Take(RowsPerPage))
        {
            lock (i.Lock)
                rows.Add(new InstanceRow(i.MapId, i.CreatorName, i.Message, (byte)i.Members.Count, i.Guild));
        }
        session.Send(InstanceMessages.OrderList((byte)pages, page, rows));
    }

    /// <summary>Sends the state of an instance to a player, and the started packet when it has started.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="instance">The instance.</param>
    private static void SendState(GameSession session, Instance instance)
    {
        byte[] state;
        bool started;
        lock (instance.Lock)
        {
            state = InstanceMessages.State((byte)instance.State, instance.MapId, (byte)instance.Members.Count, instance.Message, instance.CreatorId);
            started = instance.State == InstanceState.Started;
        }
        session.Send(state);
        if (started)
            session.Send(InstanceMessages.Started());
    }

    /// <summary>
    /// Handles creating an instance (<c>85/3</c>): checks the scene, that the player is in none, the guild restriction, the level and the
    /// done-today mark, answers with a result code, and on success tells the other players about the new row.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code.</param>
    internal void Create(GameSession session, ReadOnlyMemory<byte> body)
    {
        var r = new PacketReader(body.Span);
        var sceneId = r.U16();
        var message = r.IsEmpty ? "" : r.PStr8();
        var character = session.ActiveCharacter!;
        var id = Id(session);
        ResetDay(session, character);
        var scene = maps.Instances.FirstOrDefault(i => i.SceneId == sceneId);
        var guild = guilds.Of(id)?.Name ?? "";
        byte code = scene is null || instances.Of(id) is not null || scene.GuildOnly && guild.Length == 0 ? Codes.Failed
            : TooLow(character, scene) ? Codes.Level
                : DoneToday(character, scene) ? Codes.DoneToday
                    : Codes.Created;
        Instance? instance = null;
        if (code == Codes.Created)
        {
            instance = instances.Create(scene!, id, character.Name, message, scene!.GuildOnly ? guild : "", GameClock.UtcNow);
            if (instance is null)
                code = Codes.NoCopy;
        }
        session.Send(InstanceMessages.InitResult(code));
        logger.LogInformation("[{Session}] {Name} creates instance {Scene} ({Title}): {Result}", session.Id, character.Name, sceneId,
            scene?.Name ?? "?", instance is null ? $"refused ({code})" : $"map {instance.MapId}");
        if (instance is not null)
        {
            SendState(session, instance);
            var pages = (byte)Math.Max(1, (instances.Waiting().Count + RowsPerPage - 1) / RowsPerPage);
            Broadcast(InstanceMessages.OrderRow(pages, new InstanceRow(instance.MapId, instance.CreatorName, instance.Message, 1, instance.Guild)), except: id);
        }
    }

    /// <summary>Sends a packet to every player who is in the world.</summary>
    /// <param name="packet">The encoded packet.</param>
    /// <param name="except">The login id of a player to leave out; null to send to everybody.</param>
    private void Broadcast(byte[] packet, int? except = null)
    {
        foreach (var s in world.OnlineSessions())
            if (s.State == SessionState.InWorld && s.Account is not null && s.Account.LoginId != except)
                s.Send(packet);
    }

    /// <summary>Handles asking for the details of an instance (<c>85/4</c>): sends its members, or the ended result when it does not exist.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code.</param>
    internal void Detail(GameSession session, ReadOnlyMemory<byte> body)
    {
        var mapId = new PacketReader(body.Span).U16();
        if (instances.ByMap(mapId) is not { } instance)
        {
            session.Send(InstanceMessages.JoinResult(InstanceJoinCodes.Ended));
            return;
        }
        List<(string, int)> members;
        lock (instance.Lock)
            members = [.. instance.Members.Select(m => (m.Name, m.Id))];
        session.Send(InstanceMessages.Detail(instance.MapId, members));
    }

    /// <summary>
    /// Handles joining an instance (<c>85/5</c>): checks that it exists and waits, has room, and that the level, the done-today mark and the
    /// guild fit; answers with a result code and tells the members about the new one.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code.</param>
    internal void Join(GameSession session, ReadOnlyMemory<byte> body)
    {
        var mapId = new PacketReader(body.Span).U16();
        var character = session.ActiveCharacter!;
        var id = Id(session);
        ResetDay(session, character);
        var instance = instances.ByMap(mapId);
        byte code;
        List<int> others = [];
        byte count = 0;
        if (instance is null)
            code = InstanceJoinCodes.Ended;
        else if (instances.Of(id) is not null)
            code = InstanceJoinCodes.Failed;
        else
        {
            lock (instance.Lock)
            {
                code = instance.State != InstanceState.Waiting ? InstanceJoinCodes.Started
                    : instance.Members.Count >= instance.Scene.MaxPlayers ? InstanceJoinCodes.Full
                        : TooLow(character, instance.Scene) ? InstanceJoinCodes.Level
                            : DoneToday(character, instance.Scene) ? InstanceJoinCodes.DoneToday
                                : instance.Scene.GuildOnly && !string.Equals(guilds.Of(id)?.Name, instance.Guild, StringComparison.Ordinal)
                                    ? InstanceJoinCodes.Guild
                                    : InstanceJoinCodes.Joined;
                if (code == InstanceJoinCodes.Joined)
                {
                    others = [.. instance.Members.Select(m => m.Id)];
                    instance.Members.Add(new InstanceMember(id, character.Name));
                    count = (byte)instance.Members.Count;
                }
            }
        }
        session.Send(InstanceMessages.JoinResult(code));
        logger.LogInformation(
            "[{Session}] {Name} joins instance {Map}: {Result}",
            session.Id,
            character.Name,
            mapId,
            code == InstanceJoinCodes.Joined ? "joined" : $"refused ({code})");
        if (code != InstanceJoinCodes.Joined)
            return;
        SendState(session, instance!);
        var joined = InstanceMessages.MemberJoined(Pages(count), count, id, mapId);
        session.Send(joined);
        foreach (var other in others)
            SendTo(other, joined);
    }

    /// <summary>Handles leaving the player's instance (<c>85/6</c>).</summary>
    /// <param name="session">The player's session.</param>
    internal void Leave(GameSession session)
    {
        if (instances.Of(Id(session)) is not { } instance)
        {
            session.Send(InstanceMessages.Quit());
            return;
        }
        RemoveMember(instance, Id(session), InstanceMessages.Quit());
    }

    /// <summary>Handles the creator removing a member from a waiting instance (<c>85/13</c>).</summary>
    /// <param name="session">The creator's session.</param>
    /// <param name="body">The packet body after the action and sub code.</param>
    internal void Kick(GameSession session, ReadOnlyMemory<byte> body)
    {
        var r = new PacketReader(body.Span);
        var mapId = r.U16();
        var target = r.I32();
        if (instances.ByMap(mapId) is not { } instance || target == Id(session))
            return;
        lock (instance.Lock)
        {
            if (instance.CreatorId != Id(session) || instance.State != InstanceState.Waiting || instance.Member(target) is null)
                return;
        }
        logger.LogInformation("[{Session}] {Name} removes {Target} from instance {Map}", session.Id, session.ActiveCharacter!.Name, target, mapId);
        RemoveMember(instance, target, InstanceMessages.Refused());
    }

    /// <summary>
    /// Removes a member from an instance: tells them and sends them out when they stand on its map, tells the others, and ends the instance
    /// when nobody is left, sending out anyone still standing on its map.
    /// </summary>
    /// <param name="instance">The instance.</param>
    /// <param name="playerId">The login id of the member to remove.</param>
    /// <param name="notice">The packet that tells the removed player why.</param>
    private void RemoveMember(Instance instance, int playerId, byte[] notice)
    {
        List<int> rest;
        byte count;
        lock (instance.Lock)
        {
            instance.Members.RemoveAll(m => m.Id == playerId);
            rest = [.. instance.Members.Select(m => m.Id)];
            count = (byte)rest.Count;
        }
        if (world.SessionOf(playerId) is { } session)
        {
            session.Send(notice);
            if (session.ActiveCharacter is { } character && character.MapId == instance.MapId)
                SendOut(session, character);
        }
        var left = InstanceMessages.MemberLeft(count, playerId, instance.MapId, playerId == instance.CreatorId);
        foreach (var other in rest)
            SendTo(other, left);
        logger.LogInformation("Instance {Map} ({Title}): player {Player} left, {Count} member(s) left", instance.MapId, instance.Scene.Name, playerId, count);
        if (count == 0)
        {
            instances.Remove(instance);
            if (instance.State == InstanceState.Waiting)
                Broadcast(InstanceMessages.OrderClosed(2, instance.MapId)); // reason
            SendOutStayers(instance);
            logger.LogInformation("Instance {Map} ({Title}) ended: no members", instance.MapId, instance.Scene.Name);
        }
    }

    /// <summary>
    /// Sends out every player still standing on the map of an instance that has ended, members or not, so that nobody is left on the map copy
    /// when it is handed to the next instance. A player in a battle stays until it ends (<see cref="AfterBattle"/>); one a leader's send-out
    /// has already taken along is skipped.
    /// </summary>
    /// <param name="instance">The instance, already removed from the registry.</param>
    private void SendOutStayers(Instance instance)
    {
        foreach (var session in world.OnMap(instance.MapId).ToList())
        {
            if (session.State != SessionState.InWorld || session.Account is null || session.ActiveCharacter is not { } character
                || character.MapId != instance.MapId)
                continue;
            logger.LogInformation("[{Session}] {Name} is sent out of the ended instance {Map}", session.Id, character.Name, instance.MapId);
            SendOut(session, character);
        }
    }

    /// <summary>
    /// Sends a player whose battle has ended out of an instance map copy they no longer belong on: its instance ended while they fought (a
    /// player in a battle is not sent out with the others), or the copy was handed to an instance they are no member of.
    /// </summary>
    /// <param name="session">The player's session, out of the battle.</param>
    public void AfterBattle(GameSession session)
    {
        if (session.Closing || session.LeavingGame || session.State != SessionState.InWorld || session.Account is null
            || session.ActiveCharacter is not { } character || maps.InstanceOfMap(character.MapId) is null)
            return;
        if (instances.ByMap(character.MapId) is { } instance)
        {
            lock (instance.Lock)
            {
                if (instance.Member(Id(session)) is not null)
                    return;
            }
        }
        logger.LogInformation("[{Session}] {Name} is sent out of instance map {Map} after a battle: no member of a live instance there", session.Id,
            character.Name, character.MapId);
        SendOut(session, character);
    }

    /// <summary>Sends a player out of an instance map to the return point, leaving their team when team members are still in the instance.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="character">The player's active character.</param>
    private void SendOut(GameSession session, Character character)
    {
        if (session.Battle is not null)
            return;
        session.Event = null;
        var id = Id(session);
        var leader = world.Teams.LeaderOf(id);
        var instance = maps.InstanceOfMap(character.MapId) is not null ? instances.ByMap(character.MapId) : null;
        if (instance is not null && leader != 0
            && world.Teams.MembersOf(leader).Append(leader).Any(m => m != id && instances.Of(m) == instance))
            world.LeaveTeam(session);
        world.ChangeMap(session, ReturnPoint(character));
    }

    /// <summary>
    /// Handles pressing Enter (<c>85/9</c>): starts the instance if it is still waiting and moves the player to its entry point. Refused, as the
    /// client refuses it (Instances::enter, cannotTeleport), when the player is busy or rides a vehicle (too busy), or stands on a map no
    /// teleport may leave (prison, maze, a house, another instance copy, Carnie, ...).
    /// </summary>
    /// <param name="session">The player's session.</param>
    internal void Enter(GameSession session)
    {
        var character = session.ActiveCharacter!;
        if (instances.Of(Id(session)) is not { } instance || character.MapId == instance.MapId)
            return;
        var busy = TravelRules.BusyRefusal(session) ?? (session.Vehicle is { Riding: true } ? "riding a vehicle" : null);
        var why = busy ?? (character.MapId == CarnieReturn.Carnie ? "Carnie cannot be left this way" : travel.LeaveRefusal(session, character));
        if (why is not null)
        {
            logger.LogWarning("[{Session}] {Name}: entering instance {Map} refused: {Why}", session.Id, character.Name, instance.MapId, why);
            session.Send(InstanceMessages.InitResult(busy is not null ? Codes.TooBusy : Codes.CannotTeleport));
            return;
        }
        lock (instance.Lock)
        {
            if (instance.ClosesUtc is not null)
            {
                session.Send(InstanceMessages.InitResult(Codes.Deleted));
                return;
            }
        }
        Start(instance, GameClock.UtcNow);
        world.ChangeMap(session, new DoorWarp(instance.MapId, instance.Scene.EntryX, instance.Scene.EntryY, 0));
    }

    /// <summary>
    /// Stores where a player came from when a map change takes them from a map outside the instances onto an instance map. It runs for every
    /// moved player, so the team members the leader takes along keep their own return point too.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="warp">The destination of the map change.</param>
    private void RememberEntry(GameSession session, DoorWarp warp)
    {
        if (session.ActiveCharacter is not { } character || maps.InstanceOfMap(warp.MapId) is null || maps.InstanceOfMap(character.MapId) is not null)
            return;
        // a house room cannot be warped back to without its room details: such a player goes to the spawn point instead
        (character.InstanceReturnMapId, character.InstanceReturnX, character.InstanceReturnY) =
            session.HouseRoom != 0 ? ((ushort)0, (ushort)0, (ushort)0) : (character.MapId, character.X, character.Y);
    }

    /// <summary>Starts a waiting instance: records the time, tells the members and removes its row from the lists.</summary>
    /// <param name="instance">The instance.</param>
    /// <param name="now">The current time.</param>
    private void Start(Instance instance, DateTime now)
    {
        List<int> members;
        lock (instance.Lock)
        {
            if (instance.State != InstanceState.Waiting)
                return;
            instance.State = InstanceState.Started;
            instance.StartedUtc = now;
            members = [.. instance.Members.Select(m => m.Id)];
        }
        logger.LogInformation("Instance {Map} ({Title}) starts with {Count} member(s): {Minutes} minutes", instance.MapId,
            instance.Scene.Name, members.Count, instance.Scene.Minutes);
        var started = InstanceMessages.Started();
        foreach (var member in members)
            SendTo(member, started);
        Broadcast(InstanceMessages.OrderClosed(1, instance.MapId));
    }

    /// <summary>Sends a player who arrived on an instance map the hidden or shown state of its npcs and the time left.</summary>
    /// <param name="session">The player's session.</param>
    private void Arrived(GameSession session)
    {
        if (session.ActiveCharacter is not { } character || instances.ByMap(character.MapId) is not { } instance)
            return;
        List<(ushort Role, bool Hidden)> npcs = [];
        int? minutes;
        lock (instance.Lock)
        {
            if (instance.Member(Id(session)) is { } member)
                npcs = [.. instance.NpcHidden.Select(n => (n.Key, n.Value)), .. member.Hidden.Select(role => (role, true))];
            minutes = instance.MinutesLeft(GameClock.UtcNow);
        }
        foreach (var (role, hidden) in npcs)
            session.Send(VisibilityPacket(role, hidden));
        if (minutes is { } m)
            session.Send(InstanceMessages.RemainTime(m));
    }

    /// <summary>Sends a page of the member list of the player's instance.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="page">The wanted page, 1-based.</param>
    internal void SendMembers(GameSession session, byte page)
    {
        if (instances.Of(Id(session)) is not { } instance)
            return;
        List<int> ids;
        lock (instance.Lock)
            ids = [.. instance.Members.Select(m => m.Id)];
        var pages = Pages(ids.Count);
        page = (byte)Math.Clamp((int)page, 1, pages);
        session.Send(InstanceMessages.MemberList(pages, page, (byte)ids.Count, [.. ids.Skip((page - 1) * MembersPerPage).Take(MembersPerPage)]));
    }

    /// <summary>Handles asking for the progress (<c>85/12</c>): sends the time left and the goals defined so far.</summary>
    /// <param name="session">The player's session.</param>
    internal void SendProgress(GameSession session)
    {
        if (instances.Of(Id(session)) is not { } instance)
            return;
        List<byte[]> packets = [];
        lock (instance.Lock)
        {
            if (instance.MinutesLeft(GameClock.UtcNow) is { } minutes)
                packets.Add(InstanceMessages.RemainTime(minutes));
            packets.AddRange(GoalPackets(instance));
        }
        foreach (var p in packets)
            session.Send(p);
    }

    /// <summary>Builds the packets that define goals and show the progress already made.</summary>
    /// <param name="instance">The instance; the caller holds its lock.</param>
    /// <param name="only">A goal index to restrict the result to; null for all defined goals.</param>
    /// <returns>The encoded packets.</returns>
    private static List<byte[]> GoalPackets(Instance instance, int? only = null)
    {
        List<byte[]> packets = [];
        foreach (var (goal, (npc, needed)) in instance.Definitions.Where(d => only is null || d.Key == only).OrderBy(d => d.Key))
        {
            if (goal == 1)
            {
                packets.Add(InstanceMessages.BossDefined(npc));
                if (instance.Goal(1) > 0)
                    packets.Add(InstanceMessages.BossDone(1, ""));
            }
            else if (Instance.ClientSlot(goal) is var slot and not 0)
            {
                packets.Add(InstanceMessages.GoalDefined(slot, (byte)Math.Clamp(needed, 0, byte.MaxValue), npc));
                if (instance.Goal(goal) > 0)
                    packets.Add(InstanceMessages.GoalCount(slot, (byte)instance.Goal(goal), ""));
            }
        }
        return packets;
    }

    /// <summary>
    /// Defines a goal of an instance, beating an npc a number of times, and sends it to the player. Ignored for a player who is no member of
    /// the instance (a stray on its map copy).
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="instance">The instance.</param>
    /// <param name="goal">The goal index.</param>
    /// <param name="npcId">The id of the npc to beat.</param>
    /// <param name="needed">The number of times it has to be beaten.</param>
    public void DefineGoal(
        GameSession session,
        Instance instance,
        int goal,
        ushort npcId,
        int needed)
    {
        List<byte[]> packets;
        lock (instance.Lock)
        {
            if (instance.Member(Id(session)) is null)
            {
                logger.LogWarning("[{Session}] {Name}: goal {Goal} of instance {Map} not defined: no member", session.Id,
                    session.ActiveCharacter?.Name, goal, instance.MapId);
                return;
            }
            instance.Definitions[goal] = (npcId, needed);
            packets = GoalPackets(instance, goal);
        }
        logger.LogDebug("Instance {Map}: goal {Goal} = beat NPC {Npc} x{Needed}", instance.MapId, goal, npcId, needed);
        foreach (var p in packets)
            session.Send(p);
    }

    /// <summary>
    /// Adds to the progress of a goal and tells every member. Reaching goal 1 completes the instance, which then closes after the closing time.
    /// Ignored for a player who is no member of the instance (a stray on its map copy), who could otherwise push another group's progress.
    /// </summary>
    /// <param name="session">The session of the player who earned the progress.</param>
    /// <param name="instance">The instance.</param>
    /// <param name="goal">The goal index.</param>
    /// <param name="value">The amount to add.</param>
    public void AddGoal(GameSession session, Instance instance, int goal, int value)
    {
        var name = session.ActiveCharacter?.Name ?? "";
        List<byte[]> packets = [];
        List<int> members;
        var completes = false;
        int count;
        lock (instance.Lock)
        {
            if (instance.Member(Id(session)) is null)
            {
                logger.LogWarning("[{Session}] {Name}: goal {Goal} +{Value} of instance {Map} refused: no member", session.Id, name, goal, value,
                    instance.MapId);
                return;
            }
            count = instance.AddGoal(goal, value);
            members = [.. instance.Members.Select(m => m.Id)];
            if (goal == 1 && count > 0 && !instance.Completed)
            {
                instance.Completed = completes = true;
                instance.ClosesUtc = GameClock.UtcNow + Instance.ClosingTime;
                if (instance.Definitions.ContainsKey(1))
                    packets.Add(InstanceMessages.BossDone(1, name));
                packets.Add(InstanceMessages.InitResult(Codes.Completed));
            }
            else if (instance.Definitions.ContainsKey(goal) && Instance.ClientSlot(goal) is var slot and not 0)
                packets.Add(InstanceMessages.GoalCount(slot, (byte)count, name));
        }
        foreach (var member in members)
            foreach (var p in packets)
                SendTo(member, p);
        logger.LogInformation("Instance {Map} ({Title}): goal {Goal} +{Value} = {Count} by {Name}{Done}", instance.MapId, instance.Scene.Name, goal, value,
            count, name, completes ? ": completed, closes in 10 minutes" : "");
    }

    /// <summary>Counts a won battle for the player in the instance they are in.</summary>
    /// <param name="session">The winner's session.</param>
    public void RecordWin(GameSession session)
    {
        if (session.ActiveCharacter is not { } character || instances.ByMap(character.MapId) is not { } instance)
            return;
        lock (instance.Lock)
        {
            if (instance.Member(Id(session)) is { } member)
                member.Wins++;
        }
    }

    /// <summary>
    /// Hides or shows an npc in the player's instance. A shared change is kept for the whole instance and sent to the other members on its map;
    /// an unshared one is kept for this member only.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="role">The npc's role index on the map.</param>
    /// <param name="hidden">True to hide the npc, false to show it.</param>
    /// <param name="shared">True when the change applies to all members.</param>
    public void NpcHidden(GameSession session, ushort role, bool hidden, bool shared)
    {
        if (session.ActiveCharacter is not { } character || instances.ByMap(character.MapId) is not { } instance)
            return;
        List<int> others;
        lock (instance.Lock)
        {
            if (instance.Member(Id(session)) is not { } member)
                return;
            if (!shared)
            {
                if (hidden)
                    member.Hidden.Add(role);
                else
                    member.Hidden.Remove(role);
                return;
            }
            instance.NpcHidden[role] = hidden;
            others = [.. instance.Members.Select(m => m.Id).Where(id => id != member.Id)];
        }
        var packet = VisibilityPacket(role, hidden);
        foreach (var other in others)
        {
            if (world.SessionOf(other) is { } s && s.ActiveCharacter?.MapId == instance.MapId)
                s.Send(packet);
        }
    }

    /// <summary>Builds the packet that hides or shows an npc.</summary>
    /// <param name="role">The npc's role index on the map.</param>
    /// <param name="hidden">True to hide the npc, false to show it.</param>
    /// <returns>The encoded packet.</returns>
    private static byte[] VisibilityPacket(ushort role, bool hidden) =>
        hidden ? EventMessages.NpcHide(role, ushort.MaxValue) : EventMessages.NpcShowFor(role, ushort.MaxValue);

    /// <summary>
    /// One timer step over all instances: starts waiting ones that are due or full, ends the time limit, warns and closes finished ones, and closes
    /// waiting ones nobody is online in.
    /// </summary>
    /// <param name="now">The current time.</param>
    public void Tick(DateTime now)
    {
        try
        {
            foreach (var instance in instances.All())
            {
                List<byte[]> notices = [];
                List<int> members;
                var close = false;
                var start = false;
                lock (instance.Lock)
                {
                    members = [.. instance.Members.Select(m => m.Id)];
                    start = instance.State == InstanceState.Waiting && members.Exists(world.IsOnline)
                        && (now - instance.CreatedUtc >= TimeSpan.FromSeconds(options.Value.InstanceStartSeconds)
                            || members.Count >= instance.Scene.MaxPlayers);
                    if (!instance.Completed && !instance.Expired && instance.EndsUtc is { } ends && now >= ends)
                    {
                        instance.Expired = true;
                        instance.ClosesUtc = now + Instance.ClosingTime;
                        notices.Add(InstanceMessages.InitResult(Codes.Expired));
                        logger.LogInformation("Instance {Map} ({Title}): time is up, closes in 10 minutes", instance.MapId, instance.Scene.Name);
                    }
                    if (instance.ClosesUtc is { } closes)
                    {
                        if (!instance.WarnedClosing && closes - now <= TimeSpan.FromMinutes(5))
                        {
                            instance.WarnedClosing = true;
                            notices.Add(InstanceMessages.InitResult(Codes.Closing));
                        }
                        close = now >= closes;
                    }
                    if (instance.State == InstanceState.Waiting && !start)
                    {
                        instance.UnattendedSinceUtc = members.Exists(world.IsOnline) ? null : instance.UnattendedSinceUtc ?? now;
                        close |= now - instance.UnattendedSinceUtc >= Instance.ClosingTime;
                    }
                }
                foreach (var member in members)
                    foreach (var p in notices)
                        SendTo(member, p);
                if (close)
                    Close(instance, members);
                else if (start)
                    Start(instance, now);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Instance tick failed");
        }
    }

    /// <summary>Removes an instance, tells its members and sends out everyone standing on its map, members or not.</summary>
    /// <param name="instance">The instance.</param>
    /// <param name="members">The login ids of its members.</param>
    private void Close(Instance instance, List<int> members)
    {
        instances.Remove(instance);
        if (instance.State == InstanceState.Waiting)
            Broadcast(InstanceMessages.OrderClosed(2, instance.MapId)); // reason
        foreach (var member in members)
        {
            if (world.SessionOf(member) is not { } session)
                continue;
            session.Send(InstanceMessages.Quit());
            if (session.ActiveCharacter is { } character && character.MapId == instance.MapId)
                SendOut(session, character);
        }
        SendOutStayers(instance);
        logger.LogInformation("Instance {Map} ({Title}) closed", instance.MapId, instance.Scene.Name);
    }

    /// <summary>Sends a packet to a player if they are online.</summary>
    /// <param name="playerId">The player's login id.</param>
    /// <param name="packet">The encoded packet.</param>
    private void SendTo(int playerId, byte[] packet)
    {
        if (world.SessionOf(playerId) is { } session)
            session.Send(packet);
    }

    /// <summary>Returns the number of pages needed for a count of members.</summary>
    /// <param name="count">The number of members.</param>
    /// <returns>The number of pages, at least 1.</returns>
    private static byte Pages(int count) => (byte)Math.Max(1, (count + MembersPerPage - 1) / MembersPerPage);
}
