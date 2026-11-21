using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NorthIsland.Protocol.Messages.World;

namespace NorthIsland.Server.Features.Boards;

/// <summary>Builds and sends the Top 100 ranking lists: level, money, quiz, PK and event places.</summary>
/// <param name="store">The account store the level and money lists are built from.</param>
/// <param name="rankings">The registry of the stored PK, quiz and event lists.</param>
/// <param name="world">The world state, to reach the players who are online.</param>
/// <param name="options">The server options; their server id gives the server code.</param>
/// <param name="logger">The logger.</param>
public sealed class RankingHandlers(
    IAccountStore store,
    RankingRegistry rankings,
    WorldState world,
    IOptions<ServerOptions> options,
    ILogger<RankingHandlers> logger)
{
    /// <summary>The server code sent with the event lists, the server id divided by 100.</summary>
    private byte ServerCode => (byte)(options.Value.ServerId / 100);

    /// <summary>Numbers the names from place 1.</summary>
    /// <param name="names">The names in rank order.</param>
    /// <returns>The names with their places.</returns>
    private static List<(byte Place, string Name)> Placed(IEnumerable<string> names) => [.. names.Select((n, i) => ((byte)(i + 1), n))];

    /// <summary>The length of the level and money lists, and the number of rows per page.</summary>
    public const int Size = 100, PageSize = 15;

    /// <summary>How long the sorted level and money lists are kept before they are built again from the accounts.</summary>
    public static readonly TimeSpan ListLife = TimeSpan.FromMinutes(1);

    /// <summary>The level and money lists as they were when built, copied out of the characters.</summary>
    /// <param name="Levels">The level list: account id and row, best first.</param>
    /// <param name="Money">The money list: account id and name, best first.</param>
    /// <param name="Header">The "Updated" line sent with the level list.</param>
    /// <param name="BuiltUtc">When the lists were built (UTC).</param>
    private sealed record Lists(
        List<(int Id, LevelRankRow Row)> Levels,
        List<(int Id, string Name)> Money,
        string Header,
        DateTime BuiltUtc);

    /// <summary>The lists last built; null before the first request.</summary>
    private volatile Lists? _lists;

    /// <summary>Guards the building of <see cref="_lists"/>, so that one request builds them while the others wait.</summary>
    private readonly Lock _build = new();

    /// <summary>Gets the level and money lists, building them again from every account when they are older than <see cref="ListLife"/>.</summary>
    /// <returns>The current lists.</returns>
    private Lists Current()
    {
        if (_lists is { } fresh && GameClock.UtcNow - fresh.BuiltUtc < ListLife)
            return fresh;
        lock (_build)
        {
            if (_lists is { } built && GameClock.UtcNow - built.BuiltUtc < ListLife)
                return built;
            var all = Everyone();
            var lists = new Lists(
                [.. ByLevel(all).Select(x => (x.Id, new LevelRankRow(x.Character.Job, x.Character.Level, x.Character.Name)))],
                [.. ByMoney(all).Select(x => (x.Id, x.Character.Name))],
                string.Create(CultureInfo.InvariantCulture, $"Updated {GameClock.Now:yyyy/MM/dd HH:mm}"),
                GameClock.UtcNow);
            _lists = lists;
            return lists;
        }
    }

    /// <summary>Sends the size of every list.</summary>
    /// <param name="session">The player's session.</param>
    private void SendCounts(GameSession session)
    {
        var lists = Current();
        session.Send(BoardMessages.RankCounts(lists.Levels.Count, lists.Money.Count, (byte)rankings.Quiz().Count, (byte)rankings.PkTop().Count,
            (byte)rankings.Compass().Count, 0));
    }

    /// <summary>Starts sending fresh lists to every player in the world whenever the stored rankings change.</summary>
    internal void Start()
    {
        rankings.Changed += () =>
        {
            foreach (var s in world.OnlineSessions().Where(s => s.State == SessionState.InWorld && s.Account is not null))
                Refresh(s);
        };
    }

    /// <summary>Sends the list sizes and the first pages of the quiz, PK and event lists to a player.</summary>
    /// <param name="session">The player's session.</param>
    public void Refresh(GameSession session)
    {
        SendCounts(session);
        session.Send(BoardMessages.QuizRanks(1, rankings.Quiz().Take(PageSize)));
        session.Send(BoardMessages.PkRanks(1, Placed(rankings.PkTop().Select(r => r.Name))));
        session.Send(BoardMessages.EventRanks(5, 1, ServerCode, Placed(rankings.Compass()))); // 5 kind, 1 page
    }

    /// <summary>Lists every character of every account with its account id.</summary>
    /// <returns>The characters with their account ids.</returns>
    private List<(int Id, Character Character)> Everyone() =>
        [.. store.All().SelectMany(a => a.Characters.Select(c => (a.LoginId, c)))];

    /// <summary>Sorts characters by rebirth, level and experience, then by name.</summary>
    /// <param name="all">The characters with their account ids.</param>
    /// <returns>The top characters, at most <see cref="Size"/>.</returns>
    public static List<(int Id, Character Character)> ByLevel(IEnumerable<(int Id, Character Character)> all) =>
        [.. all.OrderByDescending(x => x.Character.RebornCount).ThenByDescending(x => x.Character.Level).ThenByDescending(x => x.Character.Exp)
            .ThenBy(x => x.Character.Name, StringComparer.Ordinal).Take(Size)];

    /// <summary>Sorts characters by gold on hand plus in the bank, then by name.</summary>
    /// <param name="all">The characters with their account ids.</param>
    /// <returns>The top characters, at most <see cref="Size"/>.</returns>
    public static List<(int Id, Character Character)> ByMoney(IEnumerable<(int Id, Character Character)> all) =>
        [.. all.OrderByDescending(x => (long)x.Character.Gold + x.Character.BankGold).ThenBy(x => x.Character.Name, StringComparer.Ordinal).Take(Size)];

    /// <summary>Answers a ranking request for the list kind and page it names, from the kept lists.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: the list kind and the page.</param>
    internal ValueTask OnRequest(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.State != SessionState.InWorld || session.Account is null || body.Length < 2)
            return ValueTask.CompletedTask;
        var (kind, page) = (body.Span[0], Math.Max((byte)1, body.Span[1]));
        var me = session.Account.LoginId;
        int Rank<T>(List<(int Id, T Entry)> list) => list.FindIndex(x => x.Id == me) is var i and >= 0 ? i + 1 : int.MaxValue;
        switch (kind)
        {
            case 0: // 84/1 list 0: sends the size of every list
                SendCounts(session);
                break;
            case 3: // 84/1 list 3: a page of the quiz ranking
                session.Send(BoardMessages.QuizRanks(page, rankings.Quiz().Skip((page - 1) * PageSize).Take(PageSize)));
                break;
            case 4: // 84/1 list 4: the PK ranking
                session.Send(BoardMessages.PkRanks(page, Placed(rankings.PkTop().Select(r => r.Name))));
                break;
            case 5: // 84/1 list 5: the compass event ranking
                session.Send(BoardMessages.EventRanks(kind, page, ServerCode, Placed(rankings.Compass())));
                break;
            case 1: // 84/1 list 1: a page of the level ranking with the player's own rank
                var levels = Current();
                session.Send(BoardMessages.LevelRanks(page, levels.Levels.Skip((page - 1) * PageSize).Take(PageSize).Select(x => x.Row),
                    Rank(levels.Levels), levels.Header));
                break;
            case 2: // 84/1 list 2: a page of the gold ranking with the player's own rank
                var money = Current().Money;
                session.Send(BoardMessages.MoneyRanks(page, money.Skip((page - 1) * PageSize).Take(PageSize).Select(x => x.Name), Rank(money)));
                break;
            case 6: // 84/1 list 6: an empty ranking
                session.Send(BoardMessages.EmptyRanks(kind, page));
                break;
        }
        logger.LogInformation("[{Session}] top 100: kind {Kind} page {Page}", session.Id, kind, page);
        return ValueTask.CompletedTask;
    }
}
