using Microsoft.Extensions.Logging;

namespace NorthIsland.Server.Features.Boards;

/// <summary>Stores the notices of the public board and the guild boards and saves them to the document store.</summary>
public sealed class BoardRegistry
{
    /// <summary>Notices per page and the most notices a board holds, for the public board and for a guild board.</summary>
    public const int PublicPageSize = 10, PublicMaxNotices = 250, GuildPageSize = 5, GuildMaxNotices = 100;

    /// <summary>The most notices one author may have on the public board and on a guild board at the same time.</summary>
    public const int PublicMaxPerAuthor = 5, GuildMaxPerAuthor = 10;

    /// <summary>Longest notice title and text in characters, as the client's posting form allows (views/events/NoticeSendForm.xml).</summary>
    public const int MaxTitle = 30, MaxText = 151;

    /// <summary>Name of the stored document.</summary>
    private const string Document = "boards";

    /// <summary>The store the board state is saved to.</summary>
    private readonly IDocumentStore _documents;

    /// <summary>The board state, guarded by <see cref="Lock"/>.</summary>
    private readonly BoardState _state;

    /// <summary>The logger.</summary>
    private readonly ILogger<BoardRegistry> _logger;

    /// <summary>Loads the saved notices, or starts empty.</summary>
    /// <param name="documents">The document store.</param>
    /// <param name="logger">The logger.</param>
    public BoardRegistry(
        IDocumentStore documents,
        ILogger<BoardRegistry> logger)
    {
        _logger = logger;
        _documents = documents;
        _state = documents.Load<BoardState>(Document) ?? new();
    }

    /// <summary>The lock that guards the state; held while changing or reading it.</summary>
    public Lock Lock { get; } = new();

    /// <summary>Lists the notices of one board, newest first.</summary>
    /// <param name="guildId">The board id: 0 for the public board, otherwise the guild id.</param>
    /// <returns>A copy of the board's notices.</returns>
    public IReadOnlyList<Notice> Notices(uint guildId)
    {
        lock (Lock)
            return [.. _state.Notices.Where(n => n.GuildId == guildId).OrderByDescending(n => n.Id)];
    }

    /// <summary>Looks up a notice by id.</summary>
    /// <param name="id">The notice id.</param>
    /// <returns>The notice, or null when it does not exist.</returns>
    public Notice? Notice(int id)
    {
        lock (Lock)
            return _state.Notices.Find(n => n.Id == id);
    }

    /// <summary>Tells whether a player may post another notice on a board: the board and the author's share of it are not full.</summary>
    /// <param name="guildId">The board id: 0 for the public board, otherwise the guild id.</param>
    /// <param name="authorId">The author's account id.</param>
    /// <returns>True when a notice may be posted.</returns>
    public bool HasRoom(uint guildId, int authorId)
    {
        lock (Lock)
            return HasRoomLocked(guildId, authorId);
    }

    /// <summary>Tells whether a player may post another notice on a board; the caller holds <see cref="Lock"/>.</summary>
    /// <param name="guildId">The board id: 0 for the public board, otherwise the guild id.</param>
    /// <param name="authorId">The author's account id.</param>
    /// <returns>True when neither the board nor the author's share of it is full.</returns>
    private bool HasRoomLocked(uint guildId, int authorId)
    {
        var (board, mine) = (0, 0);
        foreach (var n in _state.Notices.Where(n => n.GuildId == guildId))
        {
            board++;
            if (n.AuthorId == authorId)
                mine++;
        }
        return board < (guildId == 0 ? PublicMaxNotices : GuildMaxNotices) && mine < (guildId == 0 ? PublicMaxPerAuthor : GuildMaxPerAuthor);
    }

    /// <summary>Deletes every notice of a guild board and saves when there were any.</summary>
    /// <param name="guildId">The guild id; 0 (the public board) is ignored.</param>
    /// <returns>The number of notices deleted.</returns>
    public int RemoveBoard(uint guildId)
    {
        if (guildId == 0)
            return 0;
        lock (Lock)
        {
            var removed = _state.Notices.RemoveAll(n => n.GuildId == guildId);
            if (removed > 0)
                Save();
            return removed;
        }
    }

    /// <summary>Adds a notice to a board and saves.</summary>
    /// <param name="guildId">The board id: 0 for the public board, otherwise the guild id.</param>
    /// <param name="authorId">The author's account id.</param>
    /// <param name="author">The author's character name.</param>
    /// <param name="title">The title.</param>
    /// <param name="text">The text.</param>
    /// <param name="time">The posting time as an OLE automation date.</param>
    /// <returns>The new notice, or null when the board or the author's share of it is full.</returns>
    public Notice? Post(
        uint guildId,
        int authorId,
        string author,
        string title,
        string text,
        double time)
    {
        lock (Lock)
        {
            if (!HasRoomLocked(guildId, authorId))
                return null;
            var notice = new Notice
            {
                Id = ++_state.LastNoticeId,
                GuildId = guildId,
                AuthorId = authorId,
                Author = author,
                Title = title,
                Text = text,
                Time = time
            };
            _state.Notices.Add(notice);
            Save();
            return notice;
        }
    }

    /// <summary>Deletes a notice and saves.</summary>
    /// <param name="notice">The notice to delete.</param>
    public void Remove(Notice notice)
    {
        lock (Lock)
        {
            _state.Notices.Remove(notice);
            Save();
        }
    }

    /// <summary>Writes the state to the document store; a failure is logged.</summary>
    private void Save()
    {
        try
        {
            _documents.Save(Document, _state);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Saving {Document} failed", Document);
        }
    }
}
