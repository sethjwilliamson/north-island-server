using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NorthIsland.Protocol.Messages.Events;
using NorthIsland.Protocol.Opcodes;

namespace NorthIsland.Server.Features.Minigames;

/// <summary>Runs the Q&amp;A quiz game: the host NPC joins players, questions come from the bank or as sums, and places win prizes.</summary>
/// <param name="world">The world state, for map changes and the leave event.</param>
/// <param name="maps">The map data, to find the host NPC.</param>
/// <param name="items">The item data, for names and bag stacking.</param>
/// <param name="events">The event engine, whose finished events join players.</param>
/// <param name="rankings">The rankings, which get the final order.</param>
/// <param name="calendar">The event calendar, which tells whether the quiz is open and in which opening window.</param>
/// <param name="options">The minigame options.</param>
/// <param name="logger">The logger.</param>
public sealed class QuizHandlers(
    WorldState world,
    MapData maps,
    ItemData items,
    EventEngine events,
    RankingRegistry rankings,
    EventCalendar calendar,
    IOptions<MinigameOptions> options,
    ILogger<QuizHandlers> logger)
{
    /// <summary>The prize for a range of places.</summary>
    /// <param name="From">The first place of the range.</param>
    /// <param name="To">The last place of the range.</param>
    /// <param name="Items">The item ids and counts given.</param>
    private sealed record Prize(
        int From,
        int To,
        (ushort Id, int Count)[] Items);

    /// <summary>The prizes loaded from the quiz file.</summary>
    private readonly List<Prize> _prizes = [];

    /// <summary>Map id of the Q&amp;A hall.</summary>
    public const ushort QuizMapId = 10079; // Quiz Zone

    /// <summary>NPC id of the host whose click event joins a player to the game.</summary>
    public const ushort HostNpcId = 14238; // Witty Presenter

    /// <summary>A player in the running game.</summary>
    /// <param name="session">The player's session.</param>
    private sealed class Player(
        GameSession session)
    {
        /// <summary>The player's session.</summary>
        public GameSession Session { get; } = session;

        /// <summary>The lives left.</summary>
        public int Lives { get; set; }

        /// <summary>The number of right answers.</summary>
        public int Score { get; set; }

        /// <summary>The answer (1..4) given to the current question; 0 when none yet.</summary>
        public byte Answer { get; set; }
    }

    /// <summary>Guards the prize windows.</summary>
    private readonly Lock _prizeLock = new();

    /// <summary>
    /// The opening window in which each character last won a place prize, by activity, login id and character slot; a character wins at most
    /// one prize of each activity per opening window, however many games run in it.
    /// </summary>
    private readonly Dictionary<(string Activity, int LoginId, byte Slot), string> _prizeWindows = [];

    /// <summary>Guards the player list, the washed-out list, the running and started flags and the current question.</summary>
    private readonly Lock _lock = new();

    /// <summary>The players in the game, by login id.</summary>
    private readonly Dictionary<int, Player> _players = [];

    /// <summary>Names of the players washed out so far, in the order they dropped out.</summary>
    private readonly List<string> _washedOut = [];

    /// <summary>The questions loaded from the quiz file.</summary>
    private readonly List<QuizQuestion> _bank = [];

    /// <summary>Whether a game is running, from its first player joining to its end.</summary>
    private bool _running;

    /// <summary>Whether the running game has asked its first question; from then on no one can join.</summary>
    private bool _started;

    /// <summary>The id of the current question, which answers have to quote.</summary>
    private ushort _questionId;

    /// <summary>The right answer (1..4) of the current question.</summary>
    private byte _correct;

    /// <summary>The time after which answers to the current question are too late.</summary>
    private DateTime _deadline;

    /// <summary>Loads the question bank and subscribes to the finished events and the leave event.</summary>
    internal void Start()
    {
        LoadBank();
        events.EventFinished += OnEventFinished;
        world.Leaving += s => Remove(s, "left the world");
    }

    /// <summary>Loads the questions and prizes from <c>config/quiz.json</c> next to the program; if that fails only sums are asked.</summary>
    private void LoadBank()
    {
        var path = Path.Combine(AppContext.BaseDirectory, SettingsFiles.Folder, "quiz.json");
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var q in doc.RootElement.GetProperty("questions").EnumerateArray())
            {
                var answers = q.GetProperty("answers").EnumerateArray().Select(a => a.GetString() ?? "").ToArray();
                var correct = q.GetProperty("correct").GetByte();
                if (answers.Length == 4 && correct is >= 1 and <= 4)
                    _bank.Add(new QuizQuestion(q.GetProperty("text").GetString() ?? "", answers, correct));
            }
            if (doc.RootElement.TryGetProperty("prizes", out var prizes))
                foreach (var p in prizes.EnumerateArray())
                    _prizes.Add(new Prize(p.GetProperty("from").GetInt32(), p.GetProperty("to").GetInt32(),
                        [.. p.GetProperty("items").EnumerateArray().Select(i => (i.GetProperty("id").GetUInt16(), i.GetProperty("count").GetInt32()))]));
            logger.LogInformation("Loaded {Count} Q&A questions and {Prizes} prize tiers from {Path}", _bank.Count, _prizes.Count, path);
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            logger.LogWarning("No Q&A questions from {Path} ({Error}); only sums will be asked", path, ex.Message);
        }
    }

    /// <summary>Joins the player when they finished the click event of the host NPC on the Q&amp;A map.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="run">The finished event run.</param>
    private void OnEventFinished(GameSession session, EventRun run)
    {
        if (run.MapId != QuizMapId || run.Trigger != EventEngine.TriggerClick || maps.Scene(QuizMapId) is not { } scene
            || run.Index < 1 || run.Index > scene.Npcs.Count || scene.Npcs[run.Index - 1].NpcId != HostNpcId)
            return;
        Join(session);
    }

    /// <summary>
    /// Adds the player to the game, opens their screen and starts the game when none is running. It is refused while the quiz is closed by
    /// the weekly hours and once the running game has asked its first question; a player already in the game gets the screen again.
    /// </summary>
    /// <param name="session">The player's session.</param>
    public void Join(GameSession session)
    {
        if (session.Account is not { } account || session.ActiveCharacter is not { } character)
            return;
        if (!calendar.ActivityOpen(EventCalendar.ActivityQuiz))
        {
            logger.LogWarning("[{Session}] {Name}: Q&A join while the quiz is closed", session.Id, character.Name);
            return;
        }
        bool start;
        lock (_lock)
        {
            if (!_players.ContainsKey(account.LoginId))
            {
                if (_started)
                {
                    logger.LogWarning("[{Session}] {Name}: Q&A join after the first question", session.Id, character.Name);
                    return;
                }
                _players[account.LoginId] = new Player(session) { Lives = options.Value.QuizLives };
            }
            start = !_running;
            _running = true;
        }
        session.Send(QuizMessages.Open());
        SendTop();
        logger.LogInformation("[{Session}] {Name} joins the Q&A", session.Id, character.Name);
        if (start)
            _ = Task.Run(RunGame);
    }

    /// <summary>Handles the Q&amp;A packet: a leave request, or an answer to the current question.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: the sub code, then for an answer the answer and the question id.</param>
    internal ValueTask OnQuizPacket(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.Account is null || body.Length < 1)
            return ValueTask.CompletedTask;
        switch (body.Span[0])
        {
            case Action57.QuizLeave: // 57/8/2: leave, removes them and closes the screen
                Remove(session, "left");
                session.Send(QuizMessages.Close());
                break;
            case Action57.QuizAnswer when body.Length >= 4: // 57/8/3: answer to the current question
                var answer = body.Span[1];
                var question = (ushort)(body.Span[2] | body.Span[3] << 8);
                lock (_lock)
                {
                    if (_players.TryGetValue(session.Account.LoginId, out var p) && question == _questionId && GameClock.UtcNow <= _deadline
                        && p.Answer == 0 && answer is >= 1 and <= 4)
                    {
                        p.Answer = answer;
                        break;
                    }
                }
                session.Send(QuizMessages.TooLate());
                break;
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>Removes the player from the game and updates the list of players.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="why">What the player did, used in the log line.</param>
    private void Remove(GameSession session, string why)
    {
        if (session.Account is not { } account)
            return;
        lock (_lock)
        {
            if (!_players.Remove(account.LoginId))
                return;
        }
        logger.LogInformation("[{Session}] {Name} {Why} the Q&A", session.Id, session.ActiveCharacter?.Name, why);
        SendTop();
    }

    /// <summary>
    /// Runs one game: waits for the start delay, then, when the quiz is still open, asks the questions, scores the answers, washes out players
    /// without lives, publishes the ranking, gives the prizes and sends the finalists back to the lobby. When the quiz closed during the
    /// delay, the players are sent back without a game.
    /// </summary>
    private async Task RunGame()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, options.Value.QuizStartSeconds))).ConfigureAwait(false);
            var window = calendar.OpeningWindow(EventCalendar.ActivityQuiz);
            List<Player> joined;
            lock (_lock)
            {
                _started = true;
                joined = [.. _players.Values];
            }
            if (window is null)
            {
                logger.LogWarning("Q&A closed before its first question; {Count} player(s) sent back", joined.Count);
                foreach (var p in joined)
                    await SendOut(p.Session).ConfigureAwait(false);
                return;
            }
            var asked = new HashSet<int>();
            for (var round = 1; round <= options.Value.QuizQuestions; round++)
            {
                var question = Pick(asked);
                List<Player> players;
                lock (_lock)
                {
                    foreach (var gone in _players.Values.Where(p => p.Session.ActiveCharacter?.MapId != QuizMapId).ToList())
                        _players.Remove(gone.Session.Account!.LoginId);
                    if (_players.Count == 0)
                        break;
                    _questionId++;
                    _correct = question.Correct;
                    _deadline = GameClock.UtcNow.AddSeconds(options.Value.QuizAnswerSeconds);
                    foreach (var p in _players.Values)
                        p.Answer = 0;
                    players = [.. _players.Values];
                }
                var packet = QuizMessages.Question(_questionId, question.Text, question.Answers);
                foreach (var p in players)
                    p.Session.Send(packet);

                await Task.Delay(TimeSpan.FromSeconds(options.Value.QuizAnswerSeconds + 0.5)).ConfigureAwait(false);

                List<Player> washedOut = [];
                lock (_lock)
                {
                    foreach (var p in _players.Values)
                    {
                        byte outcome = p.Answer == _correct ? (byte)1 : p.Answer == 0 ? (byte)3 : (byte)2;
                        if (outcome == 1)
                            p.Score++;
                        else
                            p.Lives--;
                        p.Session.Send(QuizMessages.Reveal(_correct, (byte)Math.Max(0, p.Lives), outcome));
                        if (p.Lives <= 0)
                            washedOut.Add(p);
                    }
                    foreach (var p in washedOut.OrderBy(p => p.Score))
                    {
                        _players.Remove(p.Session.Account!.LoginId);
                        _washedOut.Add(p.Session.ActiveCharacter?.Name ?? "");
                    }
                }
                foreach (var p in washedOut)
                {
                    p.Session.Send(QuizMessages.WashedOut());
                    await SendOut(p.Session).ConfigureAwait(false);
                    logger.LogInformation("[{Session}] {Name} is washed out of the Q&A", p.Session.Id, p.Session.ActiveCharacter?.Name);
                }
                SendTop();
                await Task.Delay(TimeSpan.FromSeconds(options.Value.QuizGapSeconds)).ConfigureAwait(false);
            }

            List<Player> finalists;
            lock (_lock)
                finalists = [.. _players.Values.OrderByDescending(p => p.Score)];
            var names = finalists.Select(p => p.Session.ActiveCharacter?.Name ?? "").ToList();
            List<string> ranked;
            lock (_lock)
                ranked = [.. names, .. Enumerable.Reverse(_washedOut)];
            if (ranked.Count > 0)
                rankings.SetQuiz(ranked);
            foreach (var p in finalists)
            {
                p.Session.Send(QuizMessages.Over());
                p.Session.Send(QuizMessages.Results(names));
            }
            if (finalists.Count > 0)
                await Task.Delay(TimeSpan.FromSeconds(options.Value.QuizGapSeconds)).ConfigureAwait(false); // the places stay on screen a moment
            for (var place = 1; place <= finalists.Count; place++)
            {
                var (p, at) = (finalists[place - 1], place);
                await SendOut(p.Session, () => GivePrize(p.Session, at, EventCalendar.ActivityQuiz, window)).ConfigureAwait(false);
            }
            logger.LogInformation("Q&A over: {Places}", string.Join(", ", finalists.Select(p => $"{p.Session.ActiveCharacter?.Name} {p.Score}")));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Q&A game failed");
        }
        finally
        {
            lock (_lock)
            {
                _players.Clear();
                _washedOut.Clear();
                _running = false;
                _started = false;
            }
        }
    }

    /// <summary>
    /// Holding the player's session gate, does the work given (a prize goes in the bag between packets), then closes their quiz screen and
    /// sends them back to the lobby when they are still in the Q&amp;A hall and their connection has not ended.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="before">Work done first under the gate, such as giving the prize; null for none.</param>
    private async Task SendOut(GameSession session, Action? before = null)
    {
        using (await session.ExclusiveAsync().ConfigureAwait(false))
        {
            // a session whose connection ended is saved for the last time: no prize and no move any more
            if (session.Closing || session.State != SessionState.InWorld || session.ActiveCharacter?.MapId != QuizMapId)
                return;
            before?.Invoke();
            session.Send(QuizMessages.Close());
            world.ChangeMap(session, EventArenas.Lobby);
        }
    }

    /// <summary>
    /// Gives the prize configured for a place, if the whole prize fits in the player's bag and the character has not won a prize of the
    /// activity in the same opening window. The caller holds the player's session gate.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="place">The final place, 1-based.</param>
    /// <param name="activity">The calendar activity the game belongs to, such as <see cref="EventCalendar.ActivityQuiz"/>.</param>
    /// <param name="window">The opening window the game started in, from <see cref="EventCalendar.OpeningWindow"/>.</param>
    public void GivePrize(GameSession session, int place, string activity, string window)
    {
        if (_prizes.Find(p => place >= p.From && place <= p.To) is not { } prize || session.ActiveCharacter is not { } character
            || session.Account is not { } account)
            return;
        var key = (activity, account.LoginId, character.Slot);
        lock (_prizeLock)
        {
            if (_prizeWindows.TryGetValue(key, out var won) && won == window)
            {
                logger.LogWarning("[{Session}] {Name}: {Activity} place {Place}, but a prize was already won in window {Window}", session.Id,
                    character.Name, activity, place, window);
                return;
            }
        }
        var trial = character.Bag.Select(b => new Data.BagItem { Slot = b.Slot, ItemId = b.ItemId, Count = b.Count, Wear = b.Wear }).ToList();
        if (!prize.Items.All(i => Bag.Add(trial, items, i.Id, i.Count).Sum(a => a.Added) == i.Count))
        {
            logger.LogInformation("[{Session}] {Name}: {Activity} place {Place}, but no room for the prize", session.Id, character.Name, activity,
                place);
            return;
        }
        lock (_prizeLock)
            _prizeWindows[key] = window;
        foreach (var (id, count) in prize.Items)
            foreach (var (stack, added) in Bag.Add(character, items, id, count))
                session.Send(
                    Protocol.Messages.Items.ItemMessages.BagAdd(
                        new Protocol.Messages.Items.BagEntry { Slot = stack.Slot, ItemId = stack.ItemId, Count = added, Wear = stack.Wear }));
        logger.LogInformation("[{Session}] {Name}: {Activity} place {Place}, prize {Items}", session.Id, character.Name, activity, place,
            string.Join(", ", prize.Items.Select(i => $"{items[i.Id]?.Name ?? i.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)} x{i.Count}")));
    }

    /// <summary>Sends every player the list of the players still in the game, best score first.</summary>
    private void SendTop()
    {
        List<Player> players;
        lock (_lock)
            players = [.. _players.Values.OrderByDescending(p => p.Score)];
        var packet = QuizMessages.Top([.. players.Select(p => p.Session.Account!.LoginId)]);
        foreach (var p in players)
            p.Session.Send(packet);
    }

    /// <summary>Picks the next question: usually one from the bank not asked yet, otherwise a generated sum.</summary>
    /// <param name="asked">The bank indexes already asked in this game; the pick is added.</param>
    /// <returns>The question.</returns>
    public QuizQuestion Pick(HashSet<int> asked)
    {
        var fresh = Enumerable.Range(0, _bank.Count).Where(i => !asked.Contains(i)).ToList();
        if (fresh.Count == 0 || GameRandom.Shared.Next(100) < options.Value.QuizMathPercent)
            return Sum();
        var index = fresh[GameRandom.Shared.Next(fresh.Count)];
        asked.Add(index);
        return _bank[index];
    }

    /// <summary>Makes an arithmetic question with four different non-negative answers in random order.</summary>
    /// <returns>The question.</returns>
    public static QuizQuestion Sum()
    {
        var r = GameRandom.Shared;
        (string Text, int Value) q = r.Next(5) switch
        {
            0 => (r.Next(100, 1000), r.Next(10, 100)) is var (a, b) ? ($"{a}-{b}=?", a - b) : default, // three-digit number minus a two-digit number
            1 => (r.Next(10, 100), r.Next(10, 100)) is var (a, b) && r.Next(1, Math.Min(50, a + b)) is var c
                ? ($"{a}+{b}-{c}=?", a + b - c)
                : default, // two-digit sum minus a smaller number
            2 => (r.Next(2, 10), r.Next(2, 10), r.Next(1, 10)) is var (a, b, d) && r.Next(1, Math.Min(10, a + b + d)) is var c
                ? ($"{a}+{b}-{c}+{d}=?", a + b - c + d)
                : default, // single digits: a plus b minus c plus d
            3 => r.Next(11, 20) is var a ? ($"{a}x{a}=?", a * a) : default, // square of a number from 11 to 19
            _ => (r.Next(11, 20), r.Next(1, 10)) is var (a, b) ? ($"{a}x{a}-{b}=?", a * a - b) : default, // that square minus a single digit
        };
        var values = new List<int> { q.Value };
        for (var tries = 0; values.Count < 4 && tries < 200; tries++)
        {
            var wrong = q.Value + (r.Next(2) == 0 ? -1 : 1) * r.Next(1, 4) * (r.Next(3) == 0 ? 10 : 1);
            if (wrong >= 0 && !values.Contains(wrong))
                values.Add(wrong);
        }
        for (var next = q.Value + 1; values.Count < 4; next++)
            if (!values.Contains(next))
                values.Add(next);
        var shuffled = values.OrderBy(_ => r.Next()).ToList();
        return new QuizQuestion(q.Text, [.. shuffled.Select(v => v.ToString(System.Globalization.CultureInfo.InvariantCulture))],
            (byte)(shuffled.IndexOf(q.Value) + 1));
    }
}
