using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NorthIsland.Protocol.IO;
using NorthIsland.Protocol.Messages.Events;
using NorthIsland.Protocol.Messages.Login;
using NorthIsland.Protocol.Messages.World;
using NorthIsland.Protocol.Opcodes;

namespace NorthIsland.Server.World;

/// <summary>Handles the in-world packets: walking, chat, the return spot choice, map change completion and the online status.</summary>
/// <param name="world">The online world.</param>
/// <param name="ground">The ground items.</param>
/// <param name="events">The event engine.</param>
/// <param name="teams">The team handlers, for team chat.</param>
/// <param name="guilds">The guild handlers, for guild chat.</param>
/// <param name="wander">The roaming NPCs.</param>
/// <param name="tents">The tent handlers.</param>
/// <param name="castles">The castle registry.</param>
/// <param name="marks">The mark data, for the castle reward flag.</param>
/// <param name="fields">The field encounters, started by walking.</param>
/// <param name="gm">The GM commands, tried on every chat line.</param>
/// <param name="maps">The map data, for the map sizes a walk must stay within.</param>
/// <param name="travel">The teleport rule, for the return spot's leave and arrival rules.</param>
/// <param name="options">The world options.</param>
/// <param name="travelOptions">The travel options, for the reach of one walk.</param>
/// <param name="chatOptions">The chat limits.</param>
/// <param name="logger">The logger.</param>
public sealed class WorldHandlers(
    WorldState world,
    GroundItems ground,
    EventEngine events,
    TeamHandlers teams,
    GuildHandlers guilds,
    NpcWander wander,
    TentHandlers tents,
    CastleRegistry castles,
    MarkData marks,
    FieldEncounters fields,
    GmCommands gm,
    MapData maps,
    TravelRules travel,
    IOptions<WorldOptions> options,
    IOptions<TravelOptions> travelOptions,
    IOptions<ChatOptions> chatOptions,
    ILogger<WorldHandlers> logger)
{
    /// <summary>
    /// The item ids of the Radio Set and the Loudspeaker, worn for world chat (the client's radioKind), and of the Transceiver, a furniture
    /// that lets a player at home chat on the world channel without a radio or SP.
    /// </summary>
    public const ushort RadioSet = 34076, Loudspeaker = 34132, Transceiver = 39070; // 34076 Radio Set, 34132 Loudspeaker, 39070 Transceiver

    /// <summary>The warp to the starter's beach (map 11016).</summary>
    public static readonly DoorWarp StartersBeach = new(11016, 500, 1000, 0); // Beach

    /// <summary>The map id of South Island, where the castle owner packet is sent on arrival.</summary>
    public const ushort SouthIsland = 60002; // South Island

    /// <summary>The warp to Carnie (map 11094).</summary>
    public static readonly DoorWarp Carnie = new(CarnieReturn.Carnie, 1141, 852, 0); // Carnie, at its arrival spot

    /// <summary>The chat pace of each session; changed only from the session's own packets, which the session gate runs one at a time.</summary>
    private readonly ConditionalWeakTable<GameSession, ChatPace> _chatPace = new();

    /// <summary>
    /// Handles the choice of a return spot (1 starter's beach, 2 the recorded spot, 3 Carnie) and warps the player there. Refused by the
    /// client's own rules for the return spot, not the full teleport rule (<see cref="TravelRules.ReturnRefusal"/>: a busy state, the Prison,
    /// the Maze, a scene with flag 4, and for Carnie the maps the client keeps from it), in a team (the client leaves it first), with no spot
    /// recorded, and when the spot is on a map no teleport may reach. A refusal is shown to the player as the client's notice that the
    /// function is not available.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code.</param>
    internal ValueTask OnReturnSpot(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.State != SessionState.InWorld || session.ActiveCharacter is not { } c || session.Account is null || body.Length < 1)
            return ValueTask.CompletedTask;
        var choice = body.Span[0];
        DoorWarp? warp = choice switch
        {
            1 => StartersBeach, // 1: the starter's beach
            2 when c.RecordMapId != 0 => new DoorWarp(c.RecordMapId, c.RecordX, c.RecordY, 0), // 2: the recorded spot, only when one exists
            3 => Carnie, // 3: Carnie
            _ => null, // any other choice: no warp
        };
        var refusal = warp is null ? "not available"
            : travel.ReturnRefusal(session, c, toCarnie: choice == 3)
                ?? (world.Teams.LeaderOf(session.Account.LoginId) != 0 ? "in a team" : travel.ArrivalRefusal(warp.MapId));
        if (refusal is not null)
        {
            logger.LogWarning("[{Session}] {Name}: return spot {Choice} refused: {Why}", session.Id, c.Name, choice, refusal);
            session.Send(EventMessages.FunctionUnavailable());
            return ValueTask.CompletedTask;
        }
        world.ChangeMap(session, warp!);
        return ValueTask.CompletedTask;
    }

    /// <summary>Handles the start of auto collecting.</summary>
    /// <param name="s">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code.</param>
    internal ValueTask OnAutoCollect(GameSession s, ReadOnlyMemory<byte> body)
    {
        world.StartAutoCollect(s);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Handles a walk: checks the walk byte, stores the new position and facing, tells the world and lets fields react. A walk sent before
    /// the client loaded its new map is dropped (its spot is on the old map); one in a battle, farther than one walk reaches from the last
    /// accepted position or outside the map is refused, and the player is put back on their position.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code.</param>
    internal ValueTask OnWalk(GameSession session, ReadOnlyMemory<byte> body)
    {
        var character = session.ActiveCharacter;
        if (session.State != SessionState.InWorld || character is null || session.Account is null)
            return ValueTask.CompletedTask;

        var walk = WalkRequest.Parse(body.Span);
        var expected = WalkRequest.ExpectedCheck(session.Account.LoginId, character.Level);
        if (walk.Check != expected)
            logger.LogWarning("[{Session}] walk check byte {Actual}, expected {Expected}", session.Id, walk.Check, expected);

        if (WorldState.IsLoadingMap(session))
        {
            logger.LogInformation("[{Session}] walk to ({X}, {Y}) sent before map {Map} loaded, dropped", session.Id, walk.X, walk.Y, character.MapId);
            return ValueTask.CompletedTask;
        }
        var reach = travelOptions.Value.WalkReachPixels;
        var refusal = session.Battle is not null ? "in a battle"
            : reach > 0 && (Math.Abs(walk.X - character.X) > reach || Math.Abs(walk.Y - character.Y) > reach)
                ? $"farther than {reach} px from ({character.X}, {character.Y})"
                : maps.MapSize(character.MapId) is { } size && (walk.X >= size.Width || walk.Y >= size.Height)
                    ? $"outside the map ({size.Width} x {size.Height})"
                    : null;
        if (refusal is not null)
        {
            logger.LogWarning("[{Session}] {Name}: walk to ({X}, {Y}) on map {Map} refused: {Why}", session.Id, character.Name, walk.X, walk.Y,
                character.MapId, refusal);
            session.Send(WorldServerMessages.Jump(session.Account.LoginId, character.MapId, character.X, character.Y));
            return ValueTask.CompletedTask;
        }

        character.X = walk.X;
        character.Y = walk.Y;
        session.Facing = walk.Direction;
        world.Walk(session, walk.Direction, walk.X, walk.Y);
        if (session.Event is null)
            fields.OnWalk(session);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Lets a world chat message out: a player must wear a radio (the Radio Set or the Loudspeaker), whose message costs SP, in a battle the
    /// fighter's; at home with a Transceiver the message is free when the radio or the SP is lacking.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="c">The player's character.</param>
    /// <returns>True when the message may go out; false when the player wears no radio or lacks the SP.</returns>
    private bool ChargeWorldChat(GameSession session, Character c)
    {
        var freeAtHome = session.Account is not null
            && TentHandlers.HouseMapOf(session.Account.LoginId) == c.MapId
            && c.Furniture.Exists(f => f.ItemId == Transceiver);
        if (!c.Equipment.Exists(e => e.Slot == Gems.AccessorySlot && e.ItemId is RadioSet or Loudspeaker && !Durability.IsDestroyed(e.Wear)))
        {
            if (!freeAtHome)
                logger.LogInformation("[{Session}] {Name}: world chat refused (no radio worn)", session.Id, c.Name);
            return freeAtHome;
        }
        var cost = options.Value.WorldChatSp;
        if (cost <= 0)
            return true;
        var fighter = session.Battle?.Players.Find(p => ReferenceEquals(p.Session, session) && !p.Left);
        var sp = fighter?.Sp ?? c.Sp;
        if (sp < cost)
        {
            if (!freeAtHome)
                logger.LogInformation("[{Session}] {Name}: world chat refused ({Sp} SP, needs {Cost})", session.Id, c.Name, sp, cost);
            return freeAtHome;
        }
        if (fighter is not null)
            fighter.Sp -= cost;
        c.Sp = Math.Max(0, c.Sp - cost);
        session.Send(NorthIsland.Protocol.Messages.Player.InnMessages.SetAttribute(Action8.AttrSp, fighter?.Sp ?? c.Sp));
        return true;
    }

    /// <summary>
    /// Counts a chat line against the player's limit: over <see cref="ChatOptions.BurstLines"/> lines in <see cref="ChatOptions.BurstSeconds"/>
    /// silences them for <see cref="ChatOptions.BlockSeconds"/> and tells the client its chat is blocked (2/8).
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="c">The player's character.</param>
    /// <param name="o">The chat limits.</param>
    /// <returns>True when the line may go on; false when it is dropped.</returns>
    private bool WithinChatPace(GameSession session, Character c, ChatOptions o)
    {
        if (o.BurstLines <= 0)
            return true;
        var now = GameClock.UtcNow;
        var pace = _chatPace.GetOrCreateValue(session);
        if (now < pace.SilencedUntilUtc)
        {
            logger.LogInformation("[{Session}] {Name}: chat line dropped, silenced until {Until:HH:mm:ss}", session.Id, c.Name, pace.SilencedUntilUtc);
            return false;
        }
        // a window of at least 1 s: a setting of 0 or less would start a new window on every line and so never silence anyone
        if (now - pace.WindowStartUtc >= TimeSpan.FromSeconds(Math.Max(1, o.BurstSeconds)))
        {
            pace.WindowStartUtc = now;
            pace.Lines = 0;
        }
        pace.Lines++;
        if (pace.Lines <= o.BurstLines)
            return true;
        pace.SilencedUntilUtc = now.AddSeconds(Math.Max(0, o.BlockSeconds));
        session.Send(WorldServerMessages.ChatBlocked());
        logger.LogWarning("[{Session}] {Name}: more than {Lines} chat lines in {Seconds} s, silenced for {Block} s", session.Id, c.Name,
            o.BurstLines, o.BurstSeconds, o.BlockSeconds);
        return false;
    }

    /// <summary>
    /// Handles a chat packet: drops a line longer than the client can type or over the player's chat pace, tries the GM commands, then relays
    /// it by channel (guild, world, local, team or whisper).
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="channel">The chat channel.</param>
    /// <param name="body">The packet body after the action and sub code.</param>
    internal async ValueTask OnChat(GameSession session, byte channel, ReadOnlyMemory<byte> body)
    {
        if (session.State != SessionState.InWorld || session.ActiveCharacter is not { } c || session.Account is null)
            return;

        var o = chatOptions.Value;
        var textBytes = channel == Action2.Whisper ? body.Length - 4 : body.Length; // a whisper starts with the i32 id of the receiver
        if (o.MaxTextBytes > 0 && textBytes > o.MaxTextBytes)
        {
            logger.LogWarning("[{Session}] {Name}: chat line of {Bytes} bytes dropped (at most {Max})", session.Id, c.Name, textBytes, o.MaxTextBytes);
            return;
        }
        if (!session.Account.Gm && !WithinChatPace(session, c, o))
            return;

        var chat = ChatMessage.Parse(channel, body.Span);
        logger.LogInformation("[{Session}] chat {Channel}{Target} {Name}: {Text}", session.Id, channel,
            chat.TargetId is { } t ? $" to {t}" : "", c.Name, chat.Text);
        if (await gm.TryHandleAsync(session, chat.Text).ConfigureAwait(false))
            return;

        switch (channel)
        {
            case Action2.Guild: // 2/guild: relayed to the guild
                guilds.Chat(session, channel, chat.Text);
                break;
            case Action2.World: // 2/world: charged, then sent to all players
                if (!ChargeWorldChat(session, c))
                    break;
                world.Chat(session, channel, sameMapOnly: false, chat.Text);
                break;
            // 2/local during the war on a Trojan War round map (the castle maps among the Trojan rooms: Trojan Round 1, 2 and 3): not relayed
            case Action2.Local when castles.WarOn && CastleRegistry.IsCastleMap(c.MapId) && CastleRegistry.IsTrojanRoom(c.MapId):
                logger.LogInformation("[{Session}] {Name}: local chat refused on Trojan War map {Map}", session.Id, c.Name, c.MapId);
                break;
            case Action2.Local: // 2/local: sent to the players on the same map
                world.Chat(session, channel, sameMapOnly: true, chat.Text);
                break;
            case Action2.Team: // 2/team: sent to the team
                teams.Chat(session, channel, chat.Text);
                break;
            case Action2.Whisper when Array.IndexOf(o.NoWhisperMaps, c.MapId) >= 0: // 2/whisper from a map without whispers: not relayed
                logger.LogInformation("[{Session}] {Name}: whisper refused on map {Map}", session.Id, c.Name, c.MapId);
                break;
            case Action2.Whisper when chat.TargetId is { } target: // 2/whisper: refused if the target blocks them, else sent to the target
                if (world.SessionOf(target) is { } whisperTarget && BlocklistHandlers.Blocks(whisperTarget.ActiveCharacter, session.Account.LoginId))
                {
                    BlocklistHandlers.Refuse(session, 1);
                    break;
                }
                if (!world.Whisper(session, target, channel, chat.Text))
                    logger.LogInformation("[{Session}] whisper target {Target} is not online", session.Id, target);
                break;
            default: // any other channel: logged, not relayed
                logger.LogInformation("[{Session}] chat channel {Channel} not relayed yet", session.Id, channel);
                break;
        }
    }

    /// <summary>
    /// Handles the client's report that it loaded the new map: marks the arrival and sends the ground items, tent state,
    /// roaming NPC positions and map events. Only a map change the server sent and that is not yet loaded counts: a repeated report (or
    /// the one for the first map, which the login already set up) would reset the encounter grace, refill pickups and restart joins.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code.</param>
    internal ValueTask OnMapChangeDone(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.State != SessionState.InWorld || session.ActiveCharacter is null)
            return ValueTask.CompletedTask;
        // no time cap here: a slow client's report of a map change still outstanding is a fair one
        if (!session.IsLoadingMap(GameClock.UtcNow, TimeSpan.MaxValue))
        {
            logger.LogInformation("[{Session}] map {Map} reported loaded with no map change outstanding, ignored", session.Id,
                session.ActiveCharacter.MapId);
            return ValueTask.CompletedTask;
        }
        session.ArrivedUtc = GameClock.UtcNow;
        world.MapLoaded(session);
        if (session.ActiveCharacter.MapId == SouthIsland)
            session.Send(castles.OwnerPacket());
        if (session.ActiveCharacter.MapId == CastleRegistry.RewardRoom
            && castles.TakeWeeklyReward(session.Account!.LoginId, session.ActiveCharacter, GameClock.Now))
        {
            marks.TrySetFlag(session, session.ActiveCharacter, CastleRegistry.RewardFlag, on: true);
            logger.LogInformation("[{Session}] {Name}: the castle's weekly reward flag", session.Id, session.ActiveCharacter.Name);
        }
        ground.SendList(session);
        tents.MapEntered(session);
        wander.SendPositions(session);
        events.EnterMap(session);
        return ValueTask.CompletedTask;
    }

    /// <summary>Stores the friend list status (0 to 3) of the player and tells everyone online.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code.</param>
    internal ValueTask OnOnlineStatus(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.ActiveCharacter is not { } character || session.Account is null || body.Length < 1)
            return ValueTask.CompletedTask;
        var status = Math.Min((byte)3, new PacketReader(body.Span).U8());
        character.IcqStatus = status;
        world.Broadcast(session, new PacketWriter(Action5.Code, Action5.StatusChanged).I32(session.Account.LoginId).U8(status).ToArray(), sameMapOnly: false);
        logger.LogInformation("[{Session}] friend-list status {Status}", session.Id, status);
        return ValueTask.CompletedTask;
    }

    /// <summary>Logs the server address the client reports seeing.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code.</param>
    internal ValueTask OnServerIpEcho(GameSession session, ReadOnlyMemory<byte> body)
    {
        logger.LogInformation("[{Session}] client sees server at {Address}", session.Id, ServerIpEcho.Parse(body.Span).Address);
        return ValueTask.CompletedTask;
    }

    /// <summary>Accepts a packet and ignores it.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code.</param>
    internal static ValueTask Accept(GameSession session, ReadOnlyMemory<byte> body) => ValueTask.CompletedTask;

    /// <summary>The chat lines a player sent in the current window, and until when they are silenced.</summary>
    private sealed class ChatPace
    {
        /// <summary>When the current window began.</summary>
        public DateTime WindowStartUtc { get; set; }

        /// <summary>The lines sent in the current window.</summary>
        public int Lines { get; set; }

        /// <summary>Until when the player's lines are dropped.</summary>
        public DateTime SilencedUntilUtc { get; set; }
    }
}
