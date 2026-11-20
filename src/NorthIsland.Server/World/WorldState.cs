using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NorthIsland.Protocol.Messages.Events;
using NorthIsland.Protocol.Messages.Items;
using NorthIsland.Protocol.Messages.Player;
using NorthIsland.Protocol.Messages.World;

namespace NorthIsland.Server.World;

/// <summary>The online world: who is online and where, and the packets passed between players on the same map or in the same team.</summary>
/// <param name="logger">The logger.</param>
public sealed class WorldState(
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

    /// <summary>The accounts of the online players.</summary>
    /// <returns>The accounts.</returns>
    public IReadOnlyList<Data.Account> OnlineAccounts() => [.. _online.Values.Select(s => s.Account).OfType<Data.Account>()];

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
    /// Takes a player out of the online world: ends their battle, auto collecting, fishing and team, banks their online
    /// time and tells everyone.
    /// </summary>
    /// <param name="session">The player's session.</param>
    public void Leave(GameSession session)
    {
        if (session.Account is null)
            return;
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
    /// Moves a player to a warp's map and spot: closes trades, stalls and fishing, stores the position, tells everyone and
    /// takes along team members not in battle.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="warp">The destination.</param>
    public void ChangeMap(GameSession session, DoorWarp warp)
    {
        var character = session.ActiveCharacter!;
        var id = PlayerId(session);
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
            if (_online.TryGetValue(member, out var m) && m.ActiveCharacter is not null && m.Battle is null)
                ChangeMap(m, warp);
        }
    }

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

    /// <summary>Called when a player leaves the world, before their state is cleaned up.</summary>
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

    /// <summary>The roaming monsters that are hidden now, by map and role, with the time they come back.</summary>
    private readonly ConcurrentDictionary<(ushort Map, ushort Role), DateTime> _monstersOut = new();

    /// <summary>Whether a roaming monster is hidden at the moment; an elapsed hide is forgotten.</summary>
    /// <param name="mapId">The map id.</param>
    /// <param name="role">The monster's role number in the scene.</param>
    /// <returns>True when hidden.</returns>
    public bool MonsterIsOut(ushort mapId, ushort role)
    {
        if (!_monstersOut.TryGetValue((mapId, role), out var until))
            return false;
        if (until > GameClock.UtcNow)
            return true;
        _monstersOut.TryRemove(new KeyValuePair<(ushort, ushort), DateTime>((mapId, role), until));
        return false;
    }

    /// <summary>Hides a roaming monster for some seconds for the players on its map and remembers it.</summary>
    /// <param name="mapId">The map id.</param>
    /// <param name="role">The monster's role number in the scene.</param>
    /// <param name="seconds">How long to hide it.</param>
    /// <param name="except">Skips the players for which this returns true; null skips none.</param>
    public void HideMonster(ushort mapId, ushort role, int seconds, Func<GameSession, bool>? except = null)
    {
        var s = (ushort)Math.Clamp(seconds, 1, ushort.MaxValue - 1);
        _monstersOut[(mapId, role)] = GameClock.UtcNow.AddSeconds(s);
        var packet = EventMessages.NpcHideFor(role, s);
        foreach (var other in OnMap(mapId).Where(o => except?.Invoke(o) != true))
            other.Send(packet);
    }

    /// <summary>Shows a hidden roaming monster again to the players on its map.</summary>
    /// <param name="mapId">The map id.</param>
    /// <param name="role">The monster's role number in the scene.</param>
    public void ShowMonster(ushort mapId, ushort role)
    {
        _monstersOut.TryRemove((mapId, role), out _);
        var packet = EventMessages.NpcShowFor(role, ushort.MaxValue);
        foreach (var other in OnMap(mapId))
            other.Send(packet);
    }

    /// <summary>Tells a player the monsters of their map that are still hidden, with the seconds left.</summary>
    /// <param name="session">The player's session.</param>
    public void SendHiddenMonsters(GameSession session)
    {
        if (session.ActiveCharacter is not { } c)
            return;
        var now = GameClock.UtcNow;
        foreach (var ((map, role), until) in _monstersOut)
        {
            if (map == c.MapId && until > now)
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

    /// <summary>The sessions of the online players other than one.</summary>
    /// <param name="id">The player id to leave out.</param>
    /// <returns>The sessions.</returns>
    private IEnumerable<GameSession> Others(int id) => _online.Where(kv => kv.Key != id).Select(kv => kv.Value);

    /// <summary>The player id of a session, which is its account's login id.</summary>
    /// <param name="session">The player's session.</param>
    /// <returns>The player id.</returns>
    private static int PlayerId(GameSession session) => session.Account!.LoginId;

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
