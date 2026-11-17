using NorthIsland.GameData.Events;

namespace NorthIsland.Server.Features.Events;

/// <summary>The state of one running event: where it started, the condition groups and results still to go, and what the conditions read.</summary>
/// <param name="mapId">The map the event runs on.</param>
/// <param name="trigger">The trigger code that started it.</param>
/// <param name="index">The placement index of the NPC, door or mine that started it.</param>
/// <param name="source">A name for the starter, for the log.</param>
public sealed class EventRun(
    int mapId,
    byte trigger,
    int index,
    string source)
{
    /// <summary>The map the event runs on.</summary>
    public int MapId { get; } = mapId;

    /// <summary>The trigger code that started the event.</summary>
    public byte Trigger { get; } = trigger;

    /// <summary>The placement index of the NPC, door or mine that started the event.</summary>
    public int Index { get; } = index;

    /// <summary>A name for the starter, for the log.</summary>
    public string Source { get; } = source;

    /// <summary>True when the server started the run (the lead events of a map entry) instead of a click of the player.</summary>
    public bool ServerStarted { get; init; }

    /// <summary>The NPC states already sent to the client in this run, by NPC role.</summary>
    public Dictionary<ushort, byte> SentStates { get; } = [];

    /// <summary>The NPC roles the run gave an action, to be set back to standing at the end.</summary>
    public HashSet<ushort> ActedRoles { get; } = [];

    /// <summary>Tells whether a mark id is a counter rather than a flag.</summary>
    public Func<ushort, bool> IsCounter { get; init; } = _ => false;

    /// <summary>The number of players in the player's team, at least 1.</summary>
    public int TeamPlayers { get; init; } = 1;

    /// <summary>The instance of the map; null when the map is not an instance.</summary>
    public Instance? Instance { get; init; }

    /// <summary>The player's id.</summary>
    public int PlayerId { get; init; }

    /// <summary>True when the player belongs to the guild that owns a castle.</summary>
    public bool CastleGuildMember { get; init; }

    /// <summary>The castle registry, for the castle conditions.</summary>
    public CastleRegistry? Castles { get; init; }

    /// <summary>The id of the player's guild; 0 for none.</summary>
    public uint GuildId { get; set; }

    /// <summary>The number of players in the team who are in no guild.</summary>
    public int TeamWithoutGuild { get; init; }

    /// <summary>Gives the time the date conditions of an event compare with, by event index.</summary>
    public Func<int, DateTime> ScriptNow { get; init; } = _ => GameClock.Now;

    /// <summary>The server's time now, by the calendar's clock (the day of the week of condition 20/2).</summary>
    public Func<DateTime> Clock { get; init; } = () => GameClock.Now;

    /// <summary>The point store rows the run has added so far, for the point store window it opens.</summary>
    public List<Protocol.Messages.Items.PointStoreRow> PointStoreRows { get; } = [];

    /// <summary>The condition groups not yet tested.</summary>
    public Queue<ConditionGroup> Groups { get; } = new();

    /// <summary>The result records of the group that holds, not yet run.</summary>
    public Queue<EveScriptRecord> Pending { get; } = new();

    /// <summary>True once the first record has been sent to the client.</summary>
    public bool Started { get; set; }

    /// <summary>The states of the NPCs on the map as the client has them, by NPC role.</summary>
    public IReadOnlyDictionary<ushort, byte> NpcStates { get; init; } = new Dictionary<ushort, byte>();

    /// <summary>The hire tier the last charge of the run went through for; 0 when it was refused or none ran.</summary>
    public int HireTier { get; set; }

    /// <summary>
    /// True after a warp to the same map (a move inside the map): the rest of the group waits until the client has loaded the map again,
    /// then goes on instead of the map's entry events.
    /// </summary>
    public bool WaitsForEntry { get; set; }

    /// <summary>Tells whether the player's bag has room for one more of an item: a stack of it that isn't full, or a free place.</summary>
    public Func<ushort, bool> RoomFor { get; init; } = _ => true;

    /// <summary>The item id of the vehicle the player is riding when the run starts; 0 when none.</summary>
    public ushort RidingVehicle { get; init; }

    /// <summary>True once a condition group with results has held.</summary>
    public bool AnyGroupHeld { get; set; }

    /// <summary>True when the group now running changes the state of an instance.</summary>
    public bool GroupMovesInstance { get; set; }

    /// <summary>The results of the minigames the run started, by minigame kind.</summary>
    public Dictionary<int, int> Rolls { get; } = [];

    /// <summary>The battle index the run sent to the client and is waiting on; null for none.</summary>
    public int? PendingBattle { get; set; }

    /// <summary>How the last battle of the run ended; null while none has.</summary>
    public BattleOutcome? BattleOutcome { get; set; }

    /// <summary>
    /// The event of the group the run took last. A fight's outcome belongs to the event that fought it: every event whose groups test
    /// the outcome starts its own fight, so the outcome is cleared when the run moves on to the groups of another event.
    /// </summary>
    public int? GroupEvent { get; set; }

    /// <summary>The kind of the minigame the run started and is waiting on; null for none.</summary>
    public int? PendingMinigame { get; set; }

    /// <summary>The id of the dialog that is open and waits for an answer; null for none.</summary>
    public int? OpenInteract { get; set; }

    /// <summary>The last dialog answer: the dialog id and the answer code; null for none.</summary>
    public (int Interact, int Code)? Answer { get; set; }
}
