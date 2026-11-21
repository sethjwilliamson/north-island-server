using Microsoft.Extensions.Logging;

namespace NorthIsland.Server.Features.Guilds;

/// <summary>All guilds of the server, kept in the <c>guilds</c> document; every change saves it.</summary>
public sealed class GuildRegistry
{
    /// <summary>Name of the document the guilds are saved in.</summary>
    private const string Document = "guilds";

    /// <summary>Name of the document the guild id counter is saved in, next to the guilds.</summary>
    private const string IdDocument = "guild-ids";

    /// <summary>The guild id counter, guarded by <see cref="Lock"/>.</summary>
    private readonly GuildIdState _ids;

    /// <summary>The store the guilds are saved to.</summary>
    private readonly IDocumentStore _documents;

    /// <summary>The guilds.</summary>
    private readonly List<Guild> _guilds;

    /// <summary>Logger for failed saves.</summary>
    private readonly ILogger<GuildRegistry> _logger;

    /// <summary>Loads the saved guilds.</summary>
    /// <param name="documents">The document store the guilds are loaded from and saved to.</param>
    /// <param name="logger">The logger.</param>
    public GuildRegistry(
        IDocumentStore documents,
        ILogger<GuildRegistry> logger)
    {
        _logger = logger;
        _documents = documents;
        _guilds = documents.Load<List<Guild>>(Document) ?? [];
        _ids = documents.Load<GuildIdState>(IdDocument) ?? new();
        _ids.LastId = Math.Max(_ids.LastId, _guilds.Count == 0 ? 0 : _guilds.Max(g => g.Id));
    }

    /// <summary>Guards the guilds and their members; hold it while changing them.</summary>
    public Lock Lock { get; } = new();

    /// <summary>The guild a player belongs to.</summary>
    /// <param name="playerId">The player's login id.</param>
    /// <returns>The guild, or null when the player is in none.</returns>
    public Guild? Of(int playerId)
    {
        lock (Lock)
            return _guilds.Find(g => g.Member(playerId) is not null);
    }

    /// <summary>A snapshot of all guilds.</summary>
    /// <returns>The guilds.</returns>
    public IReadOnlyList<Guild> All()
    {
        lock (Lock)
            return [.. _guilds];
    }

    /// <summary>Finds a guild by id.</summary>
    /// <param name="id">The guild id.</param>
    /// <returns>The guild, or null when there is none.</returns>
    public Guild? ById(uint id)
    {
        lock (Lock)
            return _guilds.Find(g => g.Id == id);
    }

    /// <summary>
    /// Founds a guild with the founder as lord and all five rights, after checking under the lock that the founder is in no guild, the guild limit
    /// is not reached and the name is free. The id comes from a counter that never gives out an id twice.
    /// </summary>
    /// <param name="name">The guild name.</param>
    /// <param name="leader">The founder's character.</param>
    /// <param name="leaderId">The founder's login id.</param>
    /// <param name="maxGuilds">The most guilds the server holds; 0 or less means no limit.</param>
    /// <returns>
    /// The new guild, saved, and result 0; or null and the create result code: 3 the founder is in a guild, 4 the limit is reached, 5 the name is
    /// taken.
    /// </returns>
    public (Guild? Guild, byte Result) Create(string name, Character leader, int leaderId, int maxGuilds)
    {
        lock (Lock)
        {
            if (_guilds.Exists(g => g.Member(leaderId) is not null))
                return (null, 3); // already in a guild
            if (maxGuilds > 0 && _guilds.Count >= maxGuilds)
                return (null, 4); // guild limit
            if (_guilds.Exists(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)))
                return (null, 5); // name taken
            var lord = new GuildMember { Id = leaderId, Rights = [1, 1, 1, 1, 1] };
            lord.Refresh(leader);
            var guild = new Guild
            {
                Id = ++_ids.LastId,
                Name = name,
                LeaderId = leaderId,
                Members = [lord],
                Founded = GameClock.Now.ToOADate(),
            };
            guild.RulesSince = guild.Founded;
            _guilds.Add(guild);
            Save();
            return (guild, 0);
        }
    }

    /// <summary>Adds a member to a guild if, under the lock, the guild still exists, has room and the player is in no guild; saves.</summary>
    /// <param name="guild">The guild to join.</param>
    /// <param name="member">The new member's entry.</param>
    /// <returns>True when the member was added.</returns>
    public bool TryJoin(Guild guild, GuildMember member)
    {
        lock (Lock)
        {
            if (!_guilds.Contains(guild) || guild.Members.Count >= Guild.MaxMembers || _guilds.Exists(g => g.Member(member.Id) is not null))
                return false;
            guild.Members.Add(member);
            Save();
            return true;
        }
    }

    /// <summary>Stores a new badge image and gives the guild a new badge version.</summary>
    /// <param name="guild">The guild.</param>
    /// <param name="data">The badge image bytes.</param>
    /// <returns>The new badge version.</returns>
    public byte SetBadge(Guild guild, byte[] data)
    {
        lock (Lock)
        {
            guild.Badge = [.. data];
            guild.BadgeVersion = guild.BadgeVersion == 0 ? (byte)GameRandom.Shared.Next(1, 256) : (byte)(guild.BadgeVersion % 255 + 1);
            Save();
            return guild.BadgeVersion;
        }
    }

    /// <summary>Removes a guild and saves.</summary>
    /// <param name="guild">The guild.</param>
    public void Remove(Guild guild)
    {
        lock (Lock)
        {
            _guilds.Remove(guild);
            Save();
        }
    }

    /// <summary>Saves the guilds and the guild id counter; a failure is logged.</summary>
    public void Save()
    {
        lock (Lock)
        {
            try
            {
                _documents.Save(IdDocument, _ids);
                _documents.Save(Document, _guilds);
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "Saving {Document} failed", Document);
            }
        }
    }
}
