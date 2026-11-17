using Microsoft.Extensions.Logging;
using NorthIsland.GameData.Events;
using NorthIsland.Protocol.Messages.Events;

namespace NorthIsland.Server.Features.Events;

public sealed partial class EventEngine
{
    /// <summary>Tells whether an NPC is a Treasure Box of the National Treasure maps.</summary>
    /// <param name="mapId">The map id.</param>
    /// <param name="npcId">The NPC id.</param>
    /// <returns>True for the box NPCs on maps 10073 to 10078.</returns>
    public static bool IsTreasureBox(int mapId, int npcId) => mapId is >= 10073 and <= 10078 // 10073 Treasure Room1, 10078 Treasure Room6
        && npcId is 19034 // Treasure Box
            or 19035; // Treasure Box

    /// <summary>Starts the lead events of the map the character has entered: sends the NPC states and runs the groups that hold.</summary>
    /// <param name="session">The player's session.</param>
    public void EnterMap(GameSession session)
    {
        var character = session.ActiveCharacter!;
        if (session.Event is { WaitsForEntry: true } moved && moved.MapId == character.MapId)
        {
            // Back on the same map after a move inside it: the NPC states stay, the client hears them again, and the event goes on.
            moved.WaitsForEntry = false;
            foreach (var (role, state) in session.NpcStates)
                session.Send(EventMessages.NpcState(role, state));
            // the shows and hides too: the client loaded the map again and has its NPCs as the scene places them
            foreach (var (role, (shown, seconds)) in session.NpcVisibility)
                session.Send(shown ? EventMessages.NpcShowFor(role, (ushort)Math.Clamp(seconds, 0, ushort.MaxValue)) : EventMessages.NpcHide(role, seconds));
            logger.LogInformation("[{Session}] {Source}: the map has loaded, the event goes on", session.Id, moved.Source);
            Advance(session);
            return;
        }
        session.NpcStates.Clear();
        session.NpcVisibility.Clear();
        var scene = maps.Scene(character.MapId);
        if (scene is null)
            return;
        if (session.Event is { } previous)
            logger.LogInformation("[{Session}] map entry replaces the running event of {Source}", session.Id, previous.Source);

        var run = new EventRun(character.MapId, 0, 0, $"lead events of map {character.MapId}")
        {
            ServerStarted = true,
            NpcStates = session.NpcStates,
            IsCounter = marks.IsCounter,
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
        foreach (var ev in scene.LeadEvents)
            foreach (var group in ConditionEvaluator.Groups(ev))
                run.Groups.Enqueue(group);
        SendEntryStates(session, scene, character, run);
        if (run.Groups.Count == 0)
        {
            session.Event = null;
            return;
        }
        session.Event = run;
        Advance(session);
    }

    /// <summary>
    /// Sends the NPC states for a map entry: the spawn state of the scene objects, the states set by the lead groups that hold, and the
    /// empty state of pickup objects still refilling.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="scene">The scene of the map.</param>
    /// <param name="character">The character.</param>
    /// <param name="run">The map entry run, which remembers the states sent.</param>
    private void SendEntryStates(GameSession session, EveScene scene, Character character, EventRun run)
    {
        var states = new SortedDictionary<ushort, byte>();
        if (options.Value.NpcSpawnState is >= 0 and <= 255)
            foreach (var role in SpawnRoles(scene))
                states[role] = (byte)options.Value.NpcSpawnState;
        foreach (var group in run.Groups.Where(g => ConditionEvaluator.Holds(g, character, run, logger)))
            foreach (var r in group.Results.Where(r => r.Kind == 2 && r.Arg1 == 5))
                states[r.SubKind] = (byte)r.Value;

        var now = GameClock.UtcNow;
        foreach (var ((map, role), until) in session.EmptiedObjects)
        {
            if (until <= now)
                session.EmptiedObjects.TryRemove((map, role), out _);
            else if (map == character.MapId)
                states[role] = 1;
        }

        session.NpcStates.Clear();
        foreach (var (role, state) in states)
        {
            session.Send(EventMessages.NpcState(role, state));
            run.SentStates[role] = state;
            session.NpcStates[role] = state;
        }
        if (states.Count > 0)
            logger.LogInformation("[{Session}] map {Map}: NPC states {States}", session.Id, character.MapId,
                string.Join(' ', states.Select(s => $"{s.Key}={s.Value}")));
    }

    /// <summary>Lists the NPC roles of a scene that get the spawn state: its interactive scene objects that scripts drive.</summary>
    /// <param name="scene">The scene.</param>
    /// <returns>The role numbers, counted from 1 in the order the scene places its NPCs.</returns>
    private List<ushort> SpawnRoles(EveScene scene)
    {
        var scripted = scene.Events.Concat(scene.LeadEvents)
            .SelectMany(e => e.Conditions).SelectMany(c => c.Results)
            .Where(r => r.Kind == 2).Select(r => (int)r.SubKind).ToHashSet();
        bool Driven(int role) => scene.Npcs[role - 1].EventIds.Count > 0 || scripted.Contains(role);
        var roles = Enumerable.Range(1, scene.Npcs.Count).Where(role => npcs.IsSceneObject((ushort)scene.Npcs[role - 1].NpcId)).ToList();
        var drivenKinds = roles.Where(Driven).Select(role => scene.Npcs[role - 1].NpcId).ToHashSet();
        bool ScriptedBox(ushort npcId) => npcs[npcId]?.Type == 6 && maps.ScriptedNpcIds.Contains(npcId);
        return [.. roles.Where(role => Driven(role) || drivenKinds.Contains(scene.Npcs[role - 1].NpcId)
            || ScriptedBox((ushort)scene.Npcs[role - 1].NpcId)).Select(role => (ushort)role)];
    }

    /// <summary>
    /// Marks a pickup object as empty when the group gives an item for an object that was still full, and keeps it empty for the refill time.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="run">The running event.</param>
    /// <param name="group">The condition group that holds.</param>
    private void EmptyPickup(GameSession session, EventRun run, ConditionGroup group)
    {
        if (options.Value.PickupRefillSeconds <= 0 || maps.Scene(run.MapId) is not { } scene
            || !group.Results.Any(r => r.Kind == 1 && r.SubKind == 1 && r.Arg1 == 1 && r.Value > 0))
            return;
        foreach (var c in group.Conditions.Where(c => c.Kind == 3 && c.Arg1 == 3 && c.Value == 0 && c.Arg3 is 0 or 5))
        {
            var role = c.SubKind;
            if (role < 1 || role > scene.Npcs.Count || !npcs.IsSceneObject((ushort)scene.Npcs[role - 1].NpcId)
                || group.Results.Any(r => r.Kind == 2 && r.SubKind == role && r.Arg1 == 5))
                continue;
            session.NpcStates[role] = 1;
            run.SentStates[role] = 1;
            session.EmptiedObjects[((ushort)run.MapId, role)] = GameClock.UtcNow.AddSeconds(options.Value.PickupRefillSeconds);
            session.Send(EventMessages.NpcState(role, 1));
            logger.LogInformation(
                "[{Session}] {Source}: scene object {Role} emptied for {Seconds} s",
                session.Id,
                run.Source,
                role,
                options.Value.PickupRefillSeconds);
        }
    }

    /// <summary>Sends the NPCs the run gave an action back to the standing action and queues turning them to their placement direction.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="run">The running event.</param>
    /// <returns>True when turn records were queued.</returns>
    private bool RestoreActedNpcs(GameSession session, EventRun run)
    {
        if (run.ActedRoles.Count == 0)
            return false;
        var scene = maps.Scene(run.MapId);
        var turned = false;
        foreach (var role in run.ActedRoles)
        {
            session.Send(EventMessages.NpcAction(role, StandingAction));
            if (scene is not null && role >= 1 && role <= scene.Npcs.Count && scene.Npcs[role - 1].Direction is >= 1 and <= 8 and var direction)
            {
                run.Pending.Enqueue(new EveScriptRecord(0, 2, role, 10, 3, 0, 0, 0, direction, 0)); // 2 Kind, 10 Arg1, 3 Arg2
                turned = true;
            }
        }
        run.ActedRoles.Clear();
        return turned;
    }
}
