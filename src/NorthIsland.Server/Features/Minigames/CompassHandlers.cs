using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NorthIsland.Protocol.Messages.Events;

namespace NorthIsland.Server.Features.Minigames;

/// <summary>Runs the Golden Compass quiz game: players stand on an answer quadrant and lose lives on wrong answers.</summary>
/// <param name="world">The world state, for map changes and the join and leave events.</param>
/// <param name="quiz">The Q&amp;A handler, which supplies the questions and the prizes.</param>
/// <param name="rankings">The rankings, which get the final order.</param>
/// <param name="options">The minigame options.</param>
/// <param name="logger">The logger.</param>
public sealed class CompassHandlers(
    WorldState world,
    QuizHandlers quiz,
    RankingRegistry rankings,
    IOptions<MinigameOptions> options,
    ILogger<CompassHandlers> logger)
{
    /// <summary>Map id of the Golden Compass arena.</summary>
    public const ushort CompassMapId = 10095; // Quiz Zone

    /// <summary>Centre of the play field on the map, and its half width and half height; the answer quadrants meet at the centre.</summary>
    private const int CentreX = 722, CentreY = 668, HalfWidth = 538, HalfHeight = 273;

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

        /// <summary>The current run of right answers, counting toward a new life.</summary>
        public int Streak { get; set; }
    }

    /// <summary>Works out the lives and the streak after one question.</summary>
    /// <param name="right">Whether the player answered right.</param>
    /// <param name="lives">The lives before the question.</param>
    /// <param name="streak">The run of right answers before the question.</param>
    /// <param name="maxLives">The most lives a player can have.</param>
    /// <param name="streakForLife">Right answers in a row that earn a life; 0 or less turns it off.</param>
    /// <returns>The new lives and the new streak.</returns>
    public static (int Lives, int Streak) Judge(
        bool right,
        int lives,
        int streak,
        int maxLives,
        int streakForLife)
    {
        if (!right)
            return (lives - 1, 0);
        streak++;
        return streakForLife > 0 && streak >= streakForLife ? (Math.Min(maxLives, lives + 1), 0) : (lives, streak);
    }

    /// <summary>Guards the player list, the washed-out list and the running flag.</summary>
    private readonly Lock _lock = new();

    /// <summary>The players in the game, by login id.</summary>
    private readonly Dictionary<int, Player> _players = [];

    /// <summary>Names of the players washed out so far, in the order they dropped out.</summary>
    private readonly List<string> _washedOut = [];

    /// <summary>Whether a game is running.</summary>
    private bool _running;

    /// <summary>Subscribes to the world events: players arriving at the arena join, players leaving the world are removed.</summary>
    internal void Start()
    {
        world.Arrived += OnArrived;
        world.Leaving += s => Remove(s, "left the world");
    }

    /// <summary>Handles the Golden Compass packet; a leave request removes the player and closes their screen.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: the sub code.</param>
    internal ValueTask OnPacket(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (body.Length >= 1 && body.Span[0] == 2)
        {
            Remove(session, "left");
            session.Send(CompassMessages.Close());
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>Maps a position on the arena to the answer quadrant standing there.</summary>
    /// <param name="x">The map x coordinate.</param>
    /// <param name="y">The map y coordinate.</param>
    /// <returns>The answer 1..4 (top left, top right, bottom left, bottom right), or 0 on a dividing line or outside the field.</returns>
    public static byte AnswerAt(int x, int y)
    {
        var dx = x - CentreX;
        var dy = y - CentreY;
        if (dx == 0 || dy == 0 || Math.Abs(dx) / (double)HalfWidth + Math.Abs(dy) / (double)HalfHeight > 1)
            return 0;
        return (byte)(dy < 0 ? (dx < 0 ? 1 : 2) : (dx < 0 ? 3 : 4));
    }

    /// <summary>Joins the game when the player arrived on the arena map.</summary>
    /// <param name="session">The player's session.</param>
    private void OnArrived(GameSession session)
    {
        if (session.ActiveCharacter?.MapId == CompassMapId)
            Join(session);
    }

    /// <summary>Adds the player to the game, opens their screen and starts the game when none is running.</summary>
    /// <param name="session">The player's session.</param>
    public void Join(GameSession session)
    {
        if (session.ActiveCharacter is null || session.Account is null)
            return;
        bool start;
        lock (_lock)
        {
            _players.TryAdd(session.Account.LoginId, new Player(session) { Lives = options.Value.CompassLives });
            start = !_running;
            _running = true;
        }
        session.Send(CompassMessages.Open());
        SendParticipants();
        logger.LogInformation("[{Session}] {Name} joins the Golden Compass", session.Id, session.ActiveCharacter.Name);
        if (start)
            _ = Task.Run(RunGame);
    }

    /// <summary>Removes the player from the game and updates the participant list.</summary>
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
        logger.LogInformation("[{Session}] {Name} {Why} the Golden Compass", session.Id, session.ActiveCharacter?.Name, why);
        SendParticipants();
    }

    /// <summary>
    /// Runs one game: waits for the start delay, asks the questions, judges each player by where they stand, sends washed-out
    /// players to the lobby, then publishes the ranking and gives the prizes.
    /// </summary>
    private async Task RunGame()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(options.Value.CompassStartSeconds)).ConfigureAwait(false);
            var asked = new HashSet<int>();
            for (var round = 1; round <= options.Value.QuizQuestions; round++)
            {
                var question = quiz.Pick(asked);
                List<Player> players;
                lock (_lock)
                {
                    foreach (var gone in _players.Values.Where(p => p.Session.ActiveCharacter?.MapId != CompassMapId).ToList())
                        _players.Remove(gone.Session.Account!.LoginId);
                    if (_players.Count == 0)
                        break;
                    players = [.. _players.Values];
                }
                var packet = CompassMessages.Question(question.Text, question.Answers);
                foreach (var p in players)
                    p.Session.Send(packet);

                await Task.Delay(TimeSpan.FromSeconds(options.Value.QuizAnswerSeconds + 0.5)).ConfigureAwait(false);

                List<Player> washedOut = [];
                lock (_lock)
                {
                    foreach (var p in _players.Values)
                    {
                        var c = p.Session.ActiveCharacter!;
                        var right = AnswerAt(c.X, c.Y) == question.Correct;
                        if (right)
                            p.Score++;
                        (p.Lives, p.Streak) = Judge(right, p.Lives, p.Streak, options.Value.CompassLives, options.Value.CompassStreakForLife);
                        p.Session.Send(CompassMessages.Reveal(question.Correct, (byte)Math.Max(0, p.Lives)));
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
                    p.Session.Send(CompassMessages.WashedOut());
                    p.Session.Send(CompassMessages.Close());
                    world.ChangeMap(p.Session, EventArenas.Lobby);
                    logger.LogInformation("[{Session}] {Name} is washed out of the Golden Compass", p.Session.Id, p.Session.ActiveCharacter?.Name);
                }
                if (washedOut.Count > 0)
                    SendParticipants();
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
                rankings.SetCompass(ranked);
            for (var place = 1; place <= finalists.Count; place++)
            {
                finalists[place - 1].Session.Send(CompassMessages.Results(names));
                using (await finalists[place - 1].Session.ExclusiveAsync().ConfigureAwait(false)) // the prize goes in the bag between packets
                    quiz.GivePrize(finalists[place - 1].Session, place);
            }
            logger.LogInformation("Golden Compass over: {Places}", string.Join(", ", finalists.Select(p => $"{p.Session.ActiveCharacter?.Name} {p.Score}")));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Golden Compass game failed");
        }
        finally
        {
            lock (_lock)
            {
                _players.Clear();
                _washedOut.Clear();
                _running = false;
            }
        }
    }

    /// <summary>Sends every player the list of the players still in the game.</summary>
    private void SendParticipants()
    {
        List<Player> players;
        lock (_lock)
            players = [.. _players.Values];
        var packet = CompassMessages.Participants([.. players.Select(p => p.Session.Account!.LoginId)]);
        foreach (var p in players)
            p.Session.Send(packet);
    }
}
