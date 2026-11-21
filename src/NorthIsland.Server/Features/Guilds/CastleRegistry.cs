using System.Text.Json;
using Microsoft.Extensions.Logging;
using NorthIsland.Protocol.Messages.World;

namespace NorthIsland.Server.Features.Guilds;

/// <summary>Who owns the castle and whether the Trojan War is on; the owner guild is kept in the <c>castle</c> document.</summary>
public sealed class CastleRegistry
{
    /// <summary>JSON options for the file helpers <see cref="Load"/> and <see cref="Save"/>: indented output.</summary>
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Name of the document (and file stem) that holds the castle state.</summary>
    private const string Document = "castle";

    /// <summary>The store the castle state is saved to when the owner is cleared.</summary>
    private readonly IDocumentStore _documents;

    /// <summary>The castle state: the owner guild id.</summary>
    private readonly CastleState _state;

    /// <summary>The guild registry, used to resolve the owner guild and a player's guild.</summary>
    private readonly GuildRegistry _guilds;

    /// <summary>Logger for owner changes.</summary>
    private readonly ILogger<CastleRegistry> _logger;

    /// <summary>Guards <see cref="_state"/>.</summary>
    private readonly Lock _lock = new();

    /// <summary>Loads the saved castle state, or starts with no owner.</summary>
    /// <param name="documents">The document store the state is loaded from and saved to.</param>
    /// <param name="guilds">The registry of guilds the owner id is resolved against.</param>
    /// <param name="logger">The logger.</param>
    public CastleRegistry(
        IDocumentStore documents,
        GuildRegistry guilds,
        ILogger<CastleRegistry> logger)
    {
        _guilds = guilds;
        _logger = logger;
        _documents = documents;
        _state = documents.Load<CastleState>(Document) ?? new();
    }

    /// <summary>The path of the castle state file inside a data directory.</summary>
    /// <param name="dataDirectory">The server's data directory.</param>
    /// <returns>The full path of <c>castle.json</c> in <paramref name="dataDirectory"/>.</returns>
    public static string PathIn(string dataDirectory) => Path.Combine(dataDirectory, Document + ".json");

    /// <summary>Reads a castle state from a JSON file, for tools and tests that work on the file directly.</summary>
    /// <param name="path">The file to read.</param>
    /// <returns>The state in the file, or an empty state when the file does not exist.</returns>
    public static CastleState Load(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<CastleState>(File.ReadAllText(path)) ?? new() : new();

    /// <summary>Writes a castle state to a JSON file, creating its directory, for tools and tests that work on the file directly.</summary>
    /// <param name="path">The file to write.</param>
    /// <param name="state">The state to write.</param>
    public static void Save(string path, CastleState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(state, Json));
    }

    /// <summary>Whether a map is one of the castle's maps.</summary>
    /// <param name="mapId">The map id.</param>
    /// <returns>True for the castle maps 10065, 10066, 10068 and 11128 to 11130.</returns>
    public static bool IsCastleMap(int mapId) => mapId is 10065 // Trojan Round 1
        or 10066 // Trojan Round 2
        or 10068 // Trojan Round 3
        or >= 11128 and <= 11130; // 11128 Abyss Cave, 11130 Castle Subway

    /// <summary>Whether a map is one of the Trojan rooms, the maps 10065 to 10068.</summary>
    /// <param name="mapId">The map id.</param>
    /// <returns>True when <paramref name="mapId"/> is in that range.</returns>
    public static bool IsTrojanRoom(int mapId) => mapId is >= 10065 and <= 10068; // 10065 Trojan Round 1, 10068 Trojan Round 3

    /// <summary>The map the weekly castle reward is paid out on when a player enters it.</summary>
    public const int RewardRoom = 10068; // Trojan Round 3

    /// <summary>The story flag set when the weekly castle reward is paid out.</summary>
    public const ushort RewardFlag = 50199; // Guild war treasure given

    /// <summary>
    /// Marks the weekly castle reward as taken for the current ISO week when the player belongs to the owning guild and their account has not taken
    /// it yet; guild membership is per account, so the reward is too. Entries of earlier weeks are dropped and the state is saved.
    /// </summary>
    /// <param name="playerId">The player's login id.</param>
    /// <param name="character">
    /// The player's character; a <c>CastleRewardWeek</c> of this week, saved before the reward was kept per account, also counts as taken, and it is
    /// updated.
    /// </param>
    /// <param name="now">The current time, which decides the week.</param>
    /// <returns>True when the reward is due now, false when the account already took it this week or the player is not in the owning guild.</returns>
    public bool TakeWeeklyReward(int playerId, Data.Character character, DateTime now)
    {
        var week = System.Globalization.ISOWeek.GetYear(now) * 100 + System.Globalization.ISOWeek.GetWeekOfYear(now); // year * 100 + week
        if (character.CastleRewardWeek == week || !IsOwnerMember(playerId))
            return false;
        lock (_lock)
        {
            if (_state.RewardWeeks.TryGetValue(playerId, out var taken) && taken == week)
                return false;
            foreach (var stale in _state.RewardWeeks.Where(w => w.Value != week).Select(w => w.Key).ToList())
                _state.RewardWeeks.Remove(stale);
            _state.RewardWeeks[playerId] = week;
            _documents.Save(Document, _state);
        }
        character.CastleRewardWeek = week;
        return true;
    }

    /// <summary>Whether the Trojan War is currently on; set by the war ticker.</summary>
    public bool WarOn { get; set; }

    /// <summary>
    /// Whether a team may move from one map to another; in peace time only the owning guild's members may leave map 10065 for the castle.
    /// </summary>
    /// <param name="fromMap">The map the team leaves.</param>
    /// <param name="toMap">The map the team enters.</param>
    /// <param name="team">The login ids of the team members.</param>
    /// <returns>True when the war is on, the move does not enter the castle from map 10065, or every team member is in the owning guild.</returns>
    public bool MayPass(int fromMap, int toMap, IEnumerable<int> team) =>
        WarOn || fromMap != 10065 // Trojan Round 1
            || !(IsTrojanRoom(toMap) || IsCastleMap(toMap)) || toMap == 10065 // Trojan Round 1
            || team.All(IsOwnerMember);

    /// <summary>The guild that owns the castle, or null when there is none.</summary>
    public Guild? Owner
    {
        get
        {
            lock (_lock)
                return _state.OwnerGuildId == 0 ? null : _guilds.ById(_state.OwnerGuildId);
        }
    }

    /// <summary>Whether a player is a member of the guild that owns the castle.</summary>
    /// <param name="playerId">The player's login id.</param>
    /// <returns>True when the castle has an owner and the player belongs to that guild.</returns>
    public bool IsOwnerMember(int playerId) => Owner is { } g && _guilds.Of(playerId) == g;

    /// <summary>Builds the packet that tells the client who owns the castle.</summary>
    /// <returns>The owner packet, with the owner's badge id when the guild has a badge, else only its name (empty when there is no owner).</returns>
    public byte[] OwnerPacket() =>
        Owner is { Badge: not null } owner ? WorldServerMessages.CastleOwner(owner.Name, (int)owner.Id) : WorldServerMessages.CastleOwner(Owner?.Name ?? "");

    /// <summary>Clears the owner when the owning guild is gone, for example disbanded, and saves the state.</summary>
    /// <param name="guildId">The id of the guild that was removed.</param>
    /// <returns>The new-leader packet for a castle without an owner, or null when that guild was not the owner.</returns>
    public byte[]? GuildGone(uint guildId)
    {
        lock (_lock)
        {
            if (_state.OwnerGuildId != guildId || guildId == 0)
                return null;
            _state.OwnerGuildId = 0;
            _documents.Save(Document, _state);
        }
        _logger.LogInformation("The castle's guild {Guild} is gone: no owner", guildId);
        return WorldServerMessages.CastleNewLeader(null);
    }
}
