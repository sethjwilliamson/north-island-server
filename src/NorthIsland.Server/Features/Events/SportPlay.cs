namespace NorthIsland.Server.Features.Events;

/// <summary>
/// What the server accepts as the result of a minigame an event started (57/1): at most a win (the client's games send 1 for a win and
/// 0 for a loss, and the scripts only test for 0, 1 or above 0), and a win only after the shortest time the game can be won in.
/// </summary>
public static class SportPlay
{
    /// <summary>The highest result counted: a win.</summary>
    public const int MostResult = 1;

    /// <summary>
    /// The shortest play, in seconds from the server's start packet, after which a win can be real, by sport kind. Each game is won only
    /// when its clock runs out, so these are the clock lengths of the client's games, without their countdowns and end screens.
    /// </summary>
    private static readonly Dictionary<int, int> LeastSeconds = new()
    {
        [1] = 5, // kind 1 throwing game: lives, not a clock; 5 s is less than any knock-out of the rival's throwers takes
        [3] = 40, // kind 3 whack-a-mouse: 40 s on the clock (HitMouseGame kStartSeconds), then a 3 s end wait
        [4] = 30, // kind 4 hunter: 30 game seconds of 1.5 s each (HunterGame kTimeLeft, kSecondTicks of 30 ms frames)
        [5] = 30, // kind 5 sheep: 30 s of play (SheepGame kTimeLeft), then a 5 s game over
        [13] = 30, // kind 13 lucky game: 30 s on the clock (LuckyGame kStartTime), after a 4 s countdown
    };

    /// <summary>Gives the shortest play after which a win of a sport kind is believed.</summary>
    /// <param name="kind">The sport kind.</param>
    /// <returns>The time; zero for a kind without a clock.</returns>
    public static TimeSpan Least(int kind) => TimeSpan.FromSeconds(LeastSeconds.GetValueOrDefault(kind));

    /// <summary>Turns the result the client reported into the one the run keeps.</summary>
    /// <param name="kind">The sport kind.</param>
    /// <param name="result">The result the client reported; 0 means lost.</param>
    /// <param name="played">How long the game ran, from the server's start packet to the result.</param>
    /// <returns>The result to keep: 0 for a loss or a win too soon, else at most <see cref="MostResult"/>.</returns>
    public static int Counted(int kind, int result, TimeSpan played) =>
        result <= 0 || played < Least(kind) ? 0 : Math.Min(result, MostResult);
}
