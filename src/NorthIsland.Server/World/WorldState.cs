using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NorthIsland.Protocol.Messages.Events;
using NorthIsland.Protocol.Messages.Items;
using NorthIsland.Protocol.Messages.Player;
using NorthIsland.Protocol.Messages.World;

namespace NorthIsland.Server.World;

/// <summary>The online world: who is online and where, and the packets passed between players on the same map or in the same team.</summary>
/// <param name="travel">The teleport rule, for the maps a leader may not take the team onto and the states that keep a member back.</param>
/// <param name="maps">The map data, to tell the instance copies.</param>
/// <param name="instances">The live instances, for the members a leader may take onto an instance copy.</param>
/// <param name="logger">The logger.</param>
public sealed class WorldState(
    TravelRules travel,
    MapData maps,
    InstanceRegistry instances,
    ILogger<WorldState> logger)
{
    /// <summary>The online sessions by player id.</summary>
    private readonly ConcurrentDictionary<int, GameSession> _online = new();

    /// <summary>Held while a player enters or leaves so that arrivals and departures reach everyone consistently.</summary>
    private readonly Lock _membership = new();

    /// <summary>Whether a player is online.</summary>
    /// <param name="playerId">The player id.</param>
    /// <returns>True when online.</returns>
    public bool IsOnline(int playerId) => _online.ContainsKey(playerId);

    /// <summary>Finds the session of an online player.</summary>
    /// <param name="playerId">The player id.</param>
    /// <returns>The session, or null when offline.</returns>
    public GameSession? SessionOf(int playerId) => _online.GetValueOrDefault(playerId);

    /// <summary>
    /// Adds a player to the online world: shows them to everyone, shows them the others on the map with their pets, rides
    /// and stalls, and sends the teams and auto collectors.
    /// </summary>
    /// <param name="session">The player's session.</param>
    public void Enter(GameSession session)
    {
        var id = PlayerId(session);
        lock (_membership)
        {
            session.OnlineSinceUtc = GameClock.UtcNow;
            _online[id] = session;
            session.AttributeSent = (attr, value, value2) => OwnAttributeSent?.Invoke(session, attr, value, value2);
            foreach (var other in Others(id))
            {
                session.Send(WorldServerMessages.OtherPlayer(RecordOf(other), arriving: false));
                other.Send(WorldServerMessages.OtherPlayer(RecordOf(session), arriving: true));
                if (other.ActiveCharacter?.MapId == session.ActiveCharacter!.MapId)
                    ExchangePets(session, other);
            }
            SendRideList(session);
            SendAutoCollectors(session);
            SendSceneTeams(session);
        }
        logger.LogInformation("[{Session}] {Name} entered the world ({Count} online)", session.Id, session.ActiveCharacter!.Name, _online.Count);
    }

    /// <summary>The teams of the online players.</summary>
    public Teams Teams { get; } = new();

    /// <summary>Takes a player out of their team and tells the map and the team.</summary>
    /// <param name="session">The player's session.</param>
    public void LeaveTeam(GameSession session)
    {
        var id = PlayerId(session);
        var (leader, members) = Teams.Remove(id);
        if (leader == 0)
            return;
        SendToMapAndTeam(session, TeamMessages.Left(id), [leader, .. members]);
        logger.LogInformation("[{Session}] {Name} {What} the team of {Leader}", session.Id, session.ActiveCharacter?.Name,
            leader == id ? "disbands" : "leaves", leader);
    }

    /// <summary>
    /// Puts down every team member of a player who is not riding a vehicle of their own (15/11 with bag slot 0): a client sets its
    /// passengers' vehicle state from the driver and clears it only on the driver's 15/11, which it misses when the driver is out of view.
    /// </summary>
    /// <param name="session">The player's session, the driver who got off or a member of the team.</param>
    public void ClearPassengers(GameSession session)
    {
        var team = Teams.TeamOf(PlayerId(session));
        foreach (var memberId in team)
        {
            if (SessionOf(memberId) is { Vehicle.Riding: true })
                continue;
            SendToMapAndTeam(session, VehicleMessages.Dismounted(0, memberId), team);
        }
    }

    /// <summary>Sends a packet to a player, everyone on their map and the members of a team, each once.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="packet">The packet to send.</param>
    /// <param name="team">The player ids of the team to include.</param>
    public void SendToMapAndTeam(GameSession session, byte[] packet, IEnumerable<int> team)
    {
        var map = session.ActiveCharacter?.MapId;
        var sent = new HashSet<int>();
        foreach (var s in _online.Values.Append(session))
        {
            var sid = PlayerId(s);
            if ((ReferenceEquals(s, session) || s.ActiveCharacter?.MapId == map || team.Contains(sid)) && sent.Add(sid))
                s.Send(packet);
        }
    }

    /// <summary>Sends a player the teams on their map.</summary>
    /// <param name="session">The player's session.</param>
    private void SendSceneTeams(GameSession session)
    {
        var map = session.ActiveCharacter!.MapId;
        var here = Teams.All().Where(t => _online.TryGetValue(t.Leader, out var l) && l.ActiveCharacter?.MapId == map).ToList();
        if (here.Count > 0)
            session.Send(TeamMessages.SceneTeams(here));
    }

    /// <summary>Tells the online players who have this player as a friend that they went online or offline.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="online">True when they came online, false when they went offline.</param>
    public void TellFriends(GameSession session, bool online)
    {
        var id = PlayerId(session);
        var packet = Protocol.Messages.Player.FriendMessages.OnlineStates([(id, online)]);
        foreach (var other in Others(id))
        {
            if (other.ActiveCharacter?.Friends.Exists(f => f.Id == id) == true)
                other.Send(packet);
        }
    }

    /// <summary>
    /// Closes another player's connection (a kick by a GM or by a later login of the account) and takes them out of the world at once, under
    /// their gate, so that a later login of the account may enter the world straight away. The gate is taken before the close, as <see
    /// cref="GameSession.TryExclusiveFrom"/> refuses a closing session; when it cannot be had (the player's work holds it, or their connection
    /// already ended) the connection is only closed, and the disconnect cleanup takes them out of the world under their gate before the final save.
    /// </summary>
    /// <param name="holder">The session whose gate the caller holds (the GM's or the new login's); may be <paramref name="target"/>.</param>
    /// <param name="target">The session to close; the caller sent it the kick packet.</param>
    public void Disconnect(GameSession holder, GameSession target)
    {
        if (!target.TryExclusiveFrom(holder, out var held))
        {
            logger.LogInformation("[{Session}] closed; its disconnect cleanup takes it out of the world", target.Id);
            target.Close();
            return;
        }
        using (held)
        {
            target.Close();
            Leave(target);
        }
    }

    /// <summary>
    /// Takes a player out of the online world: ends their battle, auto collecting, fishing and team, banks their online
    /// time and tells everyone. For a session that is not the one online under its player id (not yet in the world, or
    /// already left) and not in a battle it only drops what the session itself holds; a session that lost its account is
    /// only removed. The caller holds the session's gate (<see cref="GameSession.ExclusiveAsync"/>): the disconnect
    /// cleanup, or <see cref="Disconnect"/>; the <see cref="Leaving"/> handlers change the player's state under it.
    /// </summary>
    /// <param name="session">The player's session.</param>
    public void Leave(GameSession session)
    {
        if (session.Account is null)
        {
            RemoveWithoutAccount(session);
            return;
        }
        if ((!_online.TryGetValue(PlayerId(session), out var online) || !ReferenceEquals(online, session)) && session.Battle is null)
        {
            // not in the world (yet or any more): the team, friends and map of the player id may be a newer session's by now
            session.AttributeSent = null;
            if (session.Watching is { } watching)
                lock (watching.Watchers)
                    watching.Watchers.Remove(session);
            return;
        }
        Leaving?.Invoke(session);
        if (session.Battle is { } battle)
        {
            battle.Abandon(session);
            session.Battle = null;
        }
        session.AttributeSent = null;
        StopAutoCollect(session);
        if (session.Watching is { } watched)
            lock (watched.Watchers)
                watched.Watchers.Remove(session);
        FishingHandlers.Stop(session, this);
        LeaveTeam(session);
        TellFriends(session, online: false);
        OnlineTime.Bank(session, GameClock.UtcNow);
        session.OnlineSinceUtc = null;
        lock (_membership)
        {
            if (!_online.TryRemove(new KeyValuePair<int, GameSession>(PlayerId(session), session)))
                return;
            var left = WorldServerMessages.PlayerLeft(PlayerId(session));
            foreach (var other in _online.Values)
                other.Send(left);
        }
        logger.LogInformation("[{Session}] {Name} left the world ({Count} online)", session.Id, session.ActiveCharacter?.Name, _online.Count);
    }

    /// <summary>Removes a session that is online without an account under whatever player id it was entered with, and tells everyone.</summary>
    /// <param name="session">The session.</param>
    private void RemoveWithoutAccount(GameSession session)
    {
        lock (_membership)
        {
            foreach (var entry in _online.Where(kv => ReferenceEquals(kv.Value, session)).ToList())
            {
                if (!_online.TryRemove(entry))
                    continue;
                var left = WorldServerMessages.PlayerLeft(entry.Key);
                foreach (var other in _online.Values)
                    other.Send(left);
                logger.LogWarning("[{Session}] removed from the world without an account (player id {Id})", session.Id, entry.Key);
            }
        }
    }

    /// <summary>Relays a walk to the others on the map and moves the stored position of the online team members to the walker's.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="direction">The walking direction.</param>
    /// <param name="x">The new x position.</param>
    /// <param name="y">The new y position.</param>
    public void Walk(GameSession session, byte direction, ushort x, ushort y)
    {
        StopAutoCollect(session);
        var id = PlayerId(session);
        var map = session.ActiveCharacter!.MapId;
        var packet = WorldServerMessages.PlayerWalk(id, direction, x, y);
        foreach (var other in Others(id).Where(o => o.ActiveCharacter?.MapId == map))
            other.Send(packet);
        foreach (var member in Teams.MembersOf(id))
        {
            if (_online.TryGetValue(member, out var m) && m.ActiveCharacter is { } mc && mc.MapId == map)
                (mc.X, mc.Y) = (x, y);
        }
    }

    /// <summary>Relays a chat message to the other players.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="channel">The chat channel.</param>
    /// <param name="sameMapOnly">True to send only to players on the same map.</param>
    /// <param name="text">The message text.</param>
    public void Chat(GameSession session, byte channel, bool sameMapOnly, string text)
    {
        var id = PlayerId(session);
        var map = session.ActiveCharacter!.MapId;
        var packet = WorldServerMessages.Chat(channel, id, text);
        foreach (var other in Others(id).Where(o => !sameMapOnly || o.ActiveCharacter?.MapId == map))
            other.Send(packet);
    }

    /// <summary>
    /// Moves a player to a warp's map and spot: closes trades, stalls and fishing, stores the position, tells everyone and takes along the
    /// team members that may follow (see <see cref="PullRefusal"/>).
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="warp">The destination.</param>
    public void ChangeMap(GameSession session, DoorWarp warp)
    {
        var character = session.ActiveCharacter!;
        var id = PlayerId(session);
        var (fromMap, fromRoom) = (character.MapId, session.HouseRoom);
        // a door of the leader's own event run on the map they leave, or a walk into a tent or between its rooms, may take the team onto
        // a map no teleport reaches; any other warp (GM, activity, training, send-outs) takes the team only where a teleport could go
        var walksIn = (session.Event is { Warping: true } run && run.MapId == fromMap) || TentHandlers.IsHouseMap(warp.MapId);
        MapLeaving?.Invoke(session);
        MapChanging?.Invoke(session, warp);
        StopAutoCollect(session);
        TradeHandlers.Abort(session);
        PetTradeHandlers.Abort(session);
        StallHandlers.Close(session, this);
        PetStallHandlers.Close(session, this);
        UtensilStallHandlers.Close(session, this);
        FishingHandlers.Stop(session, this);
        logger.LogInformation("[{Session}] {Name} warps {From} -> {To} ({X}, {Y}) via convey {Convey}",
            session.Id, character.Name, character.MapId, warp.MapId, warp.X, warp.Y, warp.ConveyIndex);

        session.MapChangeSent(GameClock.UtcNow);
        character.MapId = warp.MapId;
        character.X = warp.X;
        character.Y = warp.Y;
        session.HouseRoom = warp.HouseRoom;
        var packet = WorldServerMessages.MapChange(id, warp.MapId, warp.X, warp.Y, warp.ConveyIndex, warp.HouseRoom, warp.RoomSize);
        session.Send(packet);
        foreach (var other in Others(id))
            other.Send(packet);

        foreach (var member in Teams.MembersOf(id))
        {
            if (!_online.TryGetValue(member, out var m) || m.ActiveCharacter is not { } mc)
                continue;
            if (PullRefusal(m, mc, fromMap, fromRoom, warp, walksIn) is { } why)
            {
                logger.LogInformation("[{Session}] {Name} stays behind as {Leader} warps to map {To}: {Why}", m.Id, mc.Name, character.Name,
                    warp.MapId, why);
                continue;
            }
            ChangeMap(m, warp);
        }
    }

    /// <summary>
    /// Gives why a team member may not be taken along by their leader's map change: they stand on another map or house room than the one
    /// the leader leaves (members follow their leader within the scene), something keeps them busy (<see cref="FollowRefusal"/>),
    /// the destination is an instance copy they are no member of, or it is a map no teleport reaches and the warp is no walk-in. A busy
    /// member of the instance left behind outside it is told to go in through the instance window.
    /// </summary>
    /// <param name="member">The member's session.</param>
    /// <param name="character">The member's active character.</param>
    /// <param name="fromMap">The map id the leader leaves.</param>
    /// <param name="fromRoom">The house room the leader leaves, 0 outside a house.</param>
    /// <param name="warp">The leader's destination.</param>
    /// <param name="walksIn">True when the warp is a door of the leader's own event run or a walk into a tent or between its rooms.</param>
    /// <returns>The reason, or null when the member follows.</returns>
    private string? PullRefusal(
        GameSession member,
        Character character,
        ushort fromMap,
        byte fromRoom,
        DoorWarp warp,
        bool walksIn)
    {
        if (character.MapId != fromMap || member.HouseRoom != fromRoom)
            return $"on map {character.MapId} room {member.HouseRoom}, not with the leader";
        var instanceCopy = maps.InstanceOfMap(warp.MapId) is not null;
        var instanceMember = false;
        if (instanceCopy && instances.ByMap(warp.MapId) is { } instance)
        {
            lock (instance.Lock)
                instanceMember = instance.Member(PlayerId(member)) is not null;
        }
        if (FollowRefusal(member) is { } busy)
        {
            if (instanceMember && fromMap != warp.MapId) // a member left behind outside the copy goes in through the instance window
                member.Send(InstanceMessages.InitResult(InstanceInitCodes.TooBusy));
            return busy;
        }
        if (instanceCopy)
            return instanceMember ? null : $"no member of the instance on map {warp.MapId}";
        return travel.IsBarred(warp.MapId) && !walksIn ? $"map {warp.MapId} cannot be reached this way" : null;
    }

    /// <summary>
    /// Gives why a team member's state keeps them from following their leader: <see cref="TravelRules.BusyRefusal"/> without its event clause.
    /// A member often runs the same NPC event as the leader (the client mirrors the leader's NPC talk to the party), and the event engine ends
    /// a member's run when they are moved off its map, so a running event does not keep them back.
    /// </summary>
    /// <param name="member">The member's session.</param>
    /// <returns>The reason, or null when nothing holds the member.</returns>
    private static string? FollowRefusal(GameSession member) =>
        member.Battle is not null ? "in a battle"
            : member.Watching is not null ? "watching a battle"
                : member.Minigame is not null ? "in a game"
                    : member.Stalls.Any ? "at a stall"
                        : member.Trades.Items is not null || member.Trades.Pets is not null ? "trading"
                            : member.Activities.Fishing is not null ? "fishing"
                                : null;

    /// <summary>
    /// Runs when the client has loaded the map: sends the scene ready packet and the players, pets, rides, auto collectors
    /// and teams on the map, then raises Arrived.
    /// </summary>
    /// <param name="session">The player's session.</param>
    public void MapLoaded(GameSession session)
    {
        session.MapChangeLoaded();
        var map = session.ActiveCharacter!.MapId;
        session.Send(PlayerMessages.SceneReady());
        foreach (var other in Others(PlayerId(session)).Where(o => o.ActiveCharacter?.MapId == map))
        {
            session.Send(WorldServerMessages.OtherPlayer(RecordOf(other), arriving: false));
            ExchangePets(session, other);
        }
        SendRideList(session);
        SendAutoCollectors(session);
        SendSceneTeams(session);
        TellMapOfTeam(session);
        Arrived?.Invoke(session);
    }

    /// <summary>
    /// True while the player's client has not yet loaded the map it was latest moved to: a map change (7/x) is sent and its 12/1 has not
    /// come back. A team member's door or NPC trigger sent before its leader's warp reached it then names a door of the old map; run on
    /// the new map, it would start that map's door of the same index and warp the member away from the team. The wait is capped so a
    /// 12/1 that never comes cannot block the player's triggers for good.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <returns>True while the player's latest map change is not yet loaded.</returns>
    public static bool IsLoadingMap(GameSession session) =>
        session.IsLoadingMap(GameClock.UtcNow, TimeSpan.FromSeconds(10)); // 10 s: a stale trigger arrives within a round trip of the 7/x

    /// <summary>
    /// Tells the players already on the map the team of a player who has just loaded it (13/6 with that one team), so they hide its
    /// members' pets as they do for teams they saw form; a team that walks in (into a tent, say) would otherwise show its pets there.
    /// </summary>
    /// <param name="session">The player who has just loaded the map.</param>
    private void TellMapOfTeam(GameSession session)
    {
        var id = PlayerId(session);
        var leader = Teams.LeaderOf(id);
        if (leader == 0)
            return;
        var map = session.ActiveCharacter!.MapId;
        var packet = TeamMessages.SceneTeams([(leader, Teams.MembersOf(leader))]);
        foreach (var other in Others(id).Where(o => o.ActiveCharacter?.MapId == map))
            other.Send(packet);
    }

    /// <summary>Marks a player as auto collecting and tells the map.</summary>
    /// <param name="session">The player's session.</param>
    public void StartAutoCollect(GameSession session)
    {
        if (session.State != SessionState.InWorld || session.ActiveCharacter is null || session.Account is null || session.AutoCollecting)
            return;
        session.AutoCollecting = true;
        TellMap(session, ItemMessages.AutoCollecting([PlayerId(session)], started: true));
    }

    /// <summary>Ends a player's auto collecting and tells the map.</summary>
    /// <param name="session">The player's session.</param>
    public void StopAutoCollect(GameSession session)
    {
        if (!session.AutoCollecting || session.Account is null)
            return;
        session.AutoCollecting = false;
        TellMap(session, ItemMessages.AutoCollecting([PlayerId(session)], started: false));
    }

    /// <summary>Sends a packet to the other players on the player's map.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="packet">The packet to send.</param>
    private void TellMap(GameSession session, byte[] packet)
    {
        var map = session.ActiveCharacter?.MapId;
        foreach (var other in Others(PlayerId(session)).Where(o => o.ActiveCharacter?.MapId == map))
            other.Send(packet);
    }

    /// <summary>Sends a player who arrived the ids of the players on the map who are auto collecting.</summary>
    /// <param name="viewer">The arriving player's session.</param>
    private void SendAutoCollectors(GameSession viewer)
    {
        var map = viewer.ActiveCharacter!.MapId;
        var ids = Others(PlayerId(viewer)).Where(o => o.ActiveCharacter?.MapId == map && o.AutoCollecting).Select(PlayerId).ToList();
        if (ids.Count > 0)
            viewer.Send(ItemMessages.AutoCollecting(ids, started: true));
    }

    /// <summary>Called once a player's client has loaded a map.</summary>
    public Action<GameSession>? Arrived { get; set; }

    /// <summary>
    /// Called when a player leaves the world, before their state is cleaned up; the caller holds the player's gate (<see cref="Leave"/>).
    /// </summary>
    public Action<GameSession>? Leaving { get; set; }

    /// <summary>Raised when an attribute packet about the player themself is sent, with the attribute id, its value and a second value.</summary>
    public event Action<GameSession, byte, int, int>? OwnAttributeSent;

    /// <summary>Called just before a player moves to another map.</summary>
    public Action<GameSession>? MapLeaving { get; set; }

    /// <summary>Called just before a player moves to another map, with the destination; the character still holds the old map and spot.</summary>
    public Action<GameSession, DoorWarp>? MapChanging { get; set; }

    /// <summary>Called with two sessions when they come to share a map, so features can show each other their state.</summary>
    public Action<GameSession, GameSession>? PlayersMet { get; set; }

    /// <summary>Shows a player's active pet to the others on their map.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="except">Skips the players for which this returns true; null skips none.</param>
    public void ShowPet(GameSession session, Func<GameSession, bool>? except = null)
    {
        if (ScenePetOf(session) is not { } packet)
            return;
        var map = session.ActiveCharacter!.MapId;
        foreach (var other in Others(PlayerId(session)).Where(o => o.ActiveCharacter?.MapId == map && except?.Invoke(o) != true))
            other.Send(packet);
    }

    /// <summary>The equipment slot of a pet whose item the others see (the weapon, slot 3).</summary>
    public const byte ShownSlot = 3;

    /// <summary>The item id the others see on a pet out in the scene.</summary>
    /// <param name="pet">The pet.</param>
    /// <returns>The item id, 0 for none.</returns>
    public static ushort ShownItemOf(Pet pet) => pet.Equipment.Find(e => e.Slot == ShownSlot)?.ItemId ?? 0;

    /// <summary>Shows or hides the item on the player's active pet to the map after a pet equipment change.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="petSlot">The pet's slot.</param>
    /// <param name="equipSlot">The equipment slot that changed; other slots than the shown one are ignored.</param>
    public void ShowPetItem(GameSession session, byte petSlot, byte equipSlot)
    {
        if (equipSlot != ShownSlot || session.ActiveCharacter is not { } c || c.Pets.Find(p => p.Slot == petSlot) is not { } pet || pet.NpcId != c.ActivePetId)
            return;
        var item = ShownItemOf(pet);
        Broadcast(session, item != 0 ? PetMessages.ItemShown(PlayerId(session), item) : PetMessages.ItemHidden(PlayerId(session)), sameMapOnly: true);
    }

    /// <summary>Tells the others on the map to hide the player's pet.</summary>
    /// <param name="session">The player's session.</param>
    public void HidePet(GameSession session) =>
        Broadcast(session, PetMessages.ScenePetHidden(PlayerId(session)), sameMapOnly: true);

    /// <summary>Shows two players on the same map each other's pet, vehicle, ride, stalls and facing.</summary>
    /// <param name="a">The first player's session.</param>
    /// <param name="b">The second player's session.</param>
    private void ExchangePets(GameSession a, GameSession b)
    {
        PlayersMet?.Invoke(a, b);
        if (ScenePetOf(b) is { } bPet)
            a.Send(bPet);
        if (ScenePetOf(a) is { } aPet)
            b.Send(aPet);
        SendVehicle(b, a);
        SendVehicle(a, b);
        SendRide(a, b);
        SendStall(b, a);
        SendStall(a, b);
        SendFacing(b, a);
        SendFacing(a, b);
    }

    /// <summary>Sends a viewer the last direction and position a player walked.</summary>
    /// <param name="walker">The walking player's session.</param>
    /// <param name="viewer">The session to send to.</param>
    private static void SendFacing(GameSession walker, GameSession viewer)
    {
        if (walker.Facing is { } direction && walker.ActiveCharacter is { } c)
            viewer.Send(WorldServerMessages.PlayerWalk(PlayerId(walker), direction, c.X, c.Y));
    }

    /// <summary>Sends a viewer the stalls (items, pets, utensils) a player has open.</summary>
    /// <param name="owner">The stall owner's session.</param>
    /// <param name="viewer">The session to send to.</param>
    private static void SendStall(GameSession owner, GameSession viewer)
    {
        if (owner.Stalls.Items is { } s)
            viewer.Send(Protocol.Messages.Items.StallMessages.List([(PlayerId(owner), s.Name, s.Sign)]));
        if (owner.Stalls.Pets is { } ps)
            viewer.Send(Protocol.Messages.Items.StallMessages.PetStallList([(PlayerId(owner), ps.Name, ps.Sign)]));
        if (owner.Stalls.Utensils is { } us)
            viewer.Send(Protocol.Messages.Items.StallMessages.UtensilList([(PlayerId(owner), us.Name, us.Sign)]));
    }

    /// <summary>Sends a viewer the players on the map who are riding a pet.</summary>
    /// <param name="viewer">The session to send to.</param>
    private void SendRideList(GameSession viewer)
    {
        var map = viewer.ActiveCharacter!.MapId;
        var riders = Others(PlayerId(viewer)).Where(o => o.ActiveCharacter?.MapId == map && o.RidingPetSlot != 0)
            .Select(o => o.ActiveCharacter!.Pets.Find(p => p.Slot == o.RidingPetSlot) is { } pet ? (pet.Slot, PlayerId(o), pet.NpcId) : default)
            .Where(r => r.Slot != 0).ToList();
        if (riders.Count > 0)
            viewer.Send(PetMessages.RideList(riders));
    }

    /// <summary>Sends a viewer the pet a player is riding.</summary>
    /// <param name="rider">The riding player's session.</param>
    /// <param name="viewer">The session to send to.</param>
    private static void SendRide(GameSession rider, GameSession viewer)
    {
        if (rider.RidingPetSlot != 0 && rider.ActiveCharacter?.Pets.Find(p => p.Slot == rider.RidingPetSlot) is { } pet)
            viewer.Send(PetMessages.Mounted(pet.Slot, PlayerId(rider), pet.NpcId));
    }

    /// <summary>Sends a viewer the vehicle of a player when it is on the player's map.</summary>
    /// <param name="owner">The vehicle owner's session.</param>
    /// <param name="viewer">The session to send to.</param>
    private static void SendVehicle(GameSession owner, GameSession viewer)
    {
        if (owner.Vehicle is not { } v || v.MapId != owner.ActiveCharacter?.MapId)
            return;
        var id = PlayerId(owner);
        var c = owner.ActiveCharacter!;
        var (x, y) = v.Riding ? (c.X, c.Y) : (v.X, v.Y);
        viewer.Send(VehicleMessages.Placed(v.BagSlot, id, v.ItemId, x, y));
        if (v.Riding)
            viewer.Send(VehicleMessages.Boarded(v.BagSlot, id, v.ItemId));
    }

    /// <summary>Builds the packet that shows a player's active pet in the scene.</summary>
    /// <param name="session">The player's session.</param>
    /// <returns>The packet, or null without an active pet.</returns>
    private static byte[]? ScenePetOf(GameSession session) =>
        session.ActiveCharacter is { ActivePetId: not 0 } c && c.Pets.Find(p => p.NpcId == c.ActivePetId) is { } pet
            ? PetMessages.ScenePet(PlayerId(session), pet.NpcId, pet.Tradable, pet.Name, ShownItemOf(pet))
            : null;

    /// <summary>Sends a packet to the other online players.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="packet">The packet to send.</param>
    /// <param name="sameMapOnly">True to send only to players on the same map.</param>
    public void Broadcast(GameSession session, byte[] packet, bool sameMapOnly)
    {
        var map = session.ActiveCharacter!.MapId;
        foreach (var other in Others(PlayerId(session)).Where(o => !sameMapOnly || o.ActiveCharacter?.MapId == map))
            other.Send(packet);
    }

    /// <summary>Sends a whisper to an online player and an echo to the sender.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="targetId">The recipient's player id.</param>
    /// <param name="channel">The chat channel.</param>
    /// <param name="text">The message text.</param>
    /// <returns>True when the recipient is online.</returns>
    public bool Whisper(GameSession session, int targetId, byte channel, string text)
    {
        if (!_online.TryGetValue(targetId, out var target))
            return false;
        var packet = WorldServerMessages.Chat(channel, PlayerId(session), text);
        target.Send(packet);
        if (target != session)
            session.Send(packet);
        return true;
    }

    /// <summary>The player id (0) that stands for everyone in the monsters that are hidden.</summary>
    private const int Everyone = 0;

    /// <summary>The roaming monsters that are hidden now, by map, role and the player they are hidden from, with the time they come back.</summary>
    private readonly ConcurrentDictionary<(ushort Map, ushort Role, int Player), DateTime> _monstersOut = new();

    /// <summary>Whether a roaming monster is hidden from everyone at the moment; an elapsed hide is forgotten.</summary>
    /// <param name="mapId">The map id.</param>
    /// <param name="role">The monster's role number in the scene.</param>
    /// <returns>True when hidden.</returns>
    public bool MonsterIsOut(ushort mapId, ushort role) => IsOut((mapId, role, Everyone));

    /// <summary>Whether a roaming monster is hidden from one player alone at the moment, waiting for their own respawn of it.</summary>
    /// <param name="mapId">The map id.</param>
    /// <param name="role">The monster's role number in the scene.</param>
    /// <param name="player">The player id.</param>
    /// <returns>True when hidden from that player alone.</returns>
    public bool MonsterIsOutFor(ushort mapId, ushort role, int player) => IsOut((mapId, role, player));

    /// <summary>Whether a hide still runs; an elapsed one is forgotten.</summary>
    /// <param name="key">The map, the role and the player of the hide.</param>
    /// <returns>True while it runs.</returns>
    private bool IsOut((ushort Map, ushort Role, int Player) key)
    {
        if (!_monstersOut.TryGetValue(key, out var until))
            return false;
        if (until > GameClock.UtcNow)
            return true;
        _monstersOut.TryRemove(new KeyValuePair<(ushort, ushort, int), DateTime>(key, until));
        return false;
    }

    /// <summary>Hides a roaming monster for some seconds from the players on its map, or from one player alone, and remembers it.</summary>
    /// <param name="mapId">The map id.</param>
    /// <param name="role">The monster's role number in the scene.</param>
    /// <param name="seconds">How long to hide it.</param>
    /// <param name="except">Does not tell the players for which this returns true; null tells all.</param>
    /// <param name="player">The only player it is hidden from; 0 hides it from everyone.</param>
    public void HideMonster(
        ushort mapId,
        ushort role,
        int seconds,
        Func<GameSession, bool>? except = null,
        int player = Everyone)
    {
        var s = (ushort)Math.Clamp(seconds, 1, ushort.MaxValue - 1);
        _monstersOut[(mapId, role, player)] = GameClock.UtcNow.AddSeconds(s);
        var packet = EventMessages.NpcHideFor(role, s);
        foreach (var other in ViewersOf(mapId, player).Where(o => except?.Invoke(o) != true))
            other.Send(packet);
    }

    /// <summary>Shows a hidden roaming monster again to the players on its map, or to the one player it was hidden from.</summary>
    /// <param name="mapId">The map id.</param>
    /// <param name="role">The monster's role number in the scene.</param>
    /// <param name="player">The only player it was hidden from; 0 for everyone.</param>
    public void ShowMonster(ushort mapId, ushort role, int player = Everyone)
    {
        _monstersOut.TryRemove((mapId, role, player), out _);
        var packet = EventMessages.NpcShowFor(role, ushort.MaxValue);
        foreach (var other in ViewersOf(mapId, player))
            other.Send(packet);
    }

    /// <summary>The players on a map whom a hide is for.</summary>
    /// <param name="mapId">The map id.</param>
    /// <param name="player">The player it is for; 0 for everyone.</param>
    /// <returns>The sessions.</returns>
    private IEnumerable<GameSession> ViewersOf(ushort mapId, int player) =>
        player == Everyone ? OnMap(mapId)
        : SessionOf(player) is { } session && session.ActiveCharacter?.MapId == mapId ? [session]
        : [];

    /// <summary>Tells a player the monsters of their map that are still hidden from them, with the seconds left, and forgets the elapsed hides.</summary>
    /// <param name="session">The player's session.</param>
    public void SendHiddenMonsters(GameSession session)
    {
        if (session.ActiveCharacter is not { } c)
            return;
        var now = GameClock.UtcNow;
        var id = PlayerId(session);
        foreach (var (key, until) in _monstersOut)
        {
            var (map, role, player) = key;
            if (until <= now)
                _monstersOut.TryRemove(new KeyValuePair<(ushort, ushort, int), DateTime>(key, until));
            else if (map == c.MapId && (player == Everyone || player == id))
                session.Send(EventMessages.NpcHideFor(role, (ushort)Math.Clamp((int)Math.Ceiling((until - now).TotalSeconds), 1, ushort.MaxValue - 1)));
        }
    }

    /// <summary>The ids of the maps that have players.</summary>
    /// <returns>The map ids.</returns>
    public IReadOnlyCollection<ushort> OnlineMaps() =>
        [.. _online.Values.Select(s => s.ActiveCharacter?.MapId).OfType<ushort>().Distinct()];

    /// <summary>The sessions of the players on a map.</summary>
    /// <param name="mapId">The map id.</param>
    /// <returns>The sessions.</returns>
    public IEnumerable<GameSession> OnMap(ushort mapId) => _online.Values.Where(s => s.ActiveCharacter?.MapId == mapId);

    /// <summary>The sessions of all online players.</summary>
    /// <returns>The sessions.</returns>
    public IEnumerable<GameSession> OnlineSessions() => _online.Values;

    /// <summary>The sessions of the online players other than one, leaving out any session that lost its account or character.</summary>
    /// <param name="id">The player id to leave out.</param>
    /// <returns>The sessions.</returns>
    private IEnumerable<GameSession> Others(int id) =>
        _online.Where(kv => kv.Key != id && kv.Value is { Account: not null, ActiveCharacter: not null }).Select(kv => kv.Value);

    /// <summary>The player id of a session, which is its account's login id.</summary>
    /// <param name="session">The player's session.</param>
    /// <returns>The player id, or 0 (no player) when the session has no account.</returns>
    private static int PlayerId(GameSession session) => session.Account?.LoginId ?? 0;

    /// <summary>Builds the record that shows a player to the others.</summary>
    /// <param name="session">The player's session.</param>
    /// <returns>The record.</returns>
    private static OtherPlayerRecord RecordOf(GameSession session)
    {
        var c = session.ActiveCharacter!;
        return new OtherPlayerRecord
        {
            Id = PlayerId(session),
            BodyType = c.BodyType,
            Element = c.Element,
            Level = c.Level,
            MapId = c.MapId,
            X = c.X,
            Y = c.Y,
            Head = c.Head,
            Sprite = c.Sprite,
            Colour1 = c.Colour1,
            Colour2 = c.Colour2,
            EquippedItems = [.. c.EquippedItemIds()],
            PvpWins = c.PvpWins,
            OnlineStatus = c.IcqStatus,
            PvpRank = c.PvpRank,
            Reborn = c.RebornCount,
            Job = c.Job,
            Name = GmTag.Shown(c.Name, session.Account),
            Nickname = c.Nickname,
            HeartNumber = c.HeartNumber,
        };
    }
}
