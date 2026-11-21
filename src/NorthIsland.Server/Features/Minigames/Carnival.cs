using System.Collections.Immutable;

namespace NorthIsland.Server.Features.Minigames;

/// <summary>
/// The item-mall carnival machines: their sport kinds, prices, prize tables and the random draw of each machine.
/// </summary>
public static class Carnival
{
    /// <summary>Sport kinds of the Fortune Egg, Baby Dolls, Lucky 7, Golden Glove and small Lucky 7 machines.</summary>
    public const byte FortuneEgg = 6, BabyDolls = 7, Lucky7 = 8, GoldenGlove = 9, Lucky7Mini = 10;

    /// <summary>Sport kinds of the three Jigsaw machines: Jigsaw, Jigsaw 2 and Jigsaw 3.</summary>
    public const byte Jigsaw = 11, JigsawSec = 12, JigsawThird = 14;

    /// <summary>Sport kinds the item mall's Game 777 tab offers, in icon order: icon n starts <c>MallGames[n - 1]</c>.</summary>
    public static readonly ImmutableArray<byte> MallGames = [
        LuckyWheel.Kind,
        BabyDolls,
        Lucky7,
        LuckyCards.Kind,
        DiamondHit,
        FortuneEgg,
        Lucky7Mini,
        JigsawThird
    ];

    /// <summary>The value field of the sport start packet sent when a game is opened from the item mall.</summary>
    public const ushort MallGameValue = 10000;

    /// <summary>The level field of the sport start packet sent when a game is opened from the item mall.</summary>
    public const byte MallGameLevel = 1;

    /// <summary>The strip frame each Jigsaw reel (top, middle, bottom) shows at stop 1..8, indexed <c>[reel][stop - 1]</c>.</summary>
    public static readonly ImmutableArray<ImmutableArray<byte>> JigsawReels =
        [
            [4, 8, 10, 14, 2, 12, 0, 6],
            [0, 14, 12, 8, 10, 6, 4, 2],
            [10, 8, 6, 4, 0, 2, 14, 12],
        ];

    /// <summary>The Jigsaw stop (1..8) that shows the cactus; prize 9 puts it on the middle reel.</summary>
    public const byte JigsawCactus = 8;

    /// <summary>The item ids Jigsaw 3 pays, indexed by prize 0..10.</summary>
    public static readonly ImmutableArray<ushort> JigsawThirdPrizes = [30063, // Lottery Ticket
        62009, // 26% Bomb Diamond
        34152, // Tear Plier
        21159, // Defense Armor
        24125, // Gold Boots
        23132, // Gold Gloves
        34020, // Magic Set Box
        34015, // Glazed Stone Bag
        34016, // Encyclopedia
        32162, // Pizza
        33019]; // Recovery Potion

    /// <summary>Gets the prize item list of a Jigsaw machine, indexed by prize 0..10.</summary>
    /// <param name="game">The Jigsaw kind.</param>
    /// <returns>The small Lucky 7 list for Jigsaw, the Lucky 7 list for Jigsaw 2, otherwise the Jigsaw 3 list.</returns>
    public static ImmutableArray<ushort> JigsawPrizes(byte game) => game switch
    {
        Jigsaw => Lucky7MiniPrizes, // first Jigsaw uses the small Lucky 7 prizes
        JigsawSec => Lucky7Prizes, // second Jigsaw uses the Lucky 7 prizes
        _ => JigsawThirdPrizes, // any other kind: the Jigsaw 3 prizes
    };

    /// <summary>
    /// Draws a prize number: with even odds nothing (0), otherwise 1..10 where prize k has weight k out of 55.
    /// </summary>
    /// <param name="roll">Random source: <c>roll(n)</c> returns an integer from 0 to n - 1.</param>
    /// <returns>The prize number, 0 for no prize.</returns>
    public static byte PickPrize(Func<int, int> roll)
    {
        byte prize = 0;
        if (roll(2) == 1)
        {
            var pick = roll(55);
            for (prize = 1; pick >= prize; prize++)
                pick -= prize;
        }
        return prize;
    }

    /// <summary>
    /// Draws a Jigsaw result: the prize number and the three reel stops that show it. Prizes 1..8 stop on three equal
    /// stops, prize 9 shows the cactus in the middle, and a miss never shows three equal stops.
    /// </summary>
    /// <param name="roll">Random source: <c>roll(n)</c> returns an integer from 0 to n - 1.</param>
    /// <returns>The prize number (0..10) and the stops (1..8) of the top, middle and bottom reel.</returns>
    public static (byte Prize, byte[] Stops) JigsawSpin(Func<int, int> roll)
    {
        var prize = PickPrize(roll);
        if (prize is >= 1 and <= 8)
            return (prize, [prize, prize, prize]);
        if (prize == 9)
        {
            var top = (byte)(roll(8) + 1);
            var bottom = (byte)(roll(8) + 1);
            if (top == JigsawCactus && bottom == JigsawCactus)
                bottom = (byte)(roll(7) + 1);
            return (prize, [top, JigsawCactus, bottom]);
        }
        var stops = new[] { (byte)(roll(8) + 1), (byte)(roll(7) + 1), (byte)(roll(8) + 1) };
        if (stops[0] == stops[1] && stops[1] == stops[2])
            stops[2] = (byte)(stops[2] % 8 + 1);
        return (prize, stops);
    }

    /// <summary>Item ids of the Carnie Token and the Fun Token, the two tokens a play can be paid with.</summary>
    public const ushort CarnieToken = 34007, FunToken = 34008; // 34007 Carnie Token, 34008 Fun Token

    /// <summary>Gets the price of one play of a machine.</summary>
    /// <param name="game">The machine's sport kind.</param>
    /// <returns>The price in item-mall points and in tokens; (0, 0) for an unknown kind, a token price of 0 when tokens are not accepted.</returns>
    public static (int Points, int Tokens) Price(byte game) => game switch
    {
        FortuneEgg => (35, 7), // Fortune Egg: 35 points or 7 tokens
        BabyDolls => (45, 9), // Baby Dolls: 45 points or 9 tokens
        Lucky7 => (10, 2), // Lucky 7: 10 points or 2 tokens
        GoldenGlove => (75, 15), // Golden Glove: 75 points or 15 tokens
        Lucky7Mini => (5, 0), // small Lucky 7: 5 points, no tokens
        Jigsaw => (25, 0), // Jigsaw: 25 points, no tokens
        JigsawSec => (100, 20), // second Jigsaw: 100 points or 20 tokens
        JigsawThird => (10, 2), // third Jigsaw: 10 points or 2 tokens
        LuckyCards.Kind => (20, 4), // Lucky Cards: 20 points or 4 tokens
        DiamondHit => (20, 4), // Diamond Hit: 20 points or 4 tokens
        LuckyWheel.Kind => (LuckyWheel.Points, LuckyWheel.Tokens), // Lucky Wheel: its own prices
        _ => (0, 0), // unknown machine: free
    };

    /// <summary>Sport kind of the Diamond Hit machine.</summary>
    public const byte DiamondHit = 19;

    /// <summary>The item ids Diamond Hit pays, indexed by prize 0..10.</summary>
    public static readonly ImmutableArray<ushort> DiamondHitPrizes = [30063, // Lottery Ticket
        62011, // 32% Bomb Diamond
        62010, // 28% Bomb Diamond
        62007, // 22% Bomb Diamond
        62006, // 20% Bomb Diamond
        62005, // 18% Bomb Diamond
        62004, // 16% Bomb Diamond
        62003, // 14% Bomb Diamond
        62002, // 12% Bomb Diamond
        62001, // 10% Bomb Diamond
        62000]; // 8% Bomb Diamond

    /// <summary>The item ids the Fortune Egg pays; egg n (1..10) pays entry n - 1.</summary>
    public static readonly ImmutableArray<ushort> FortuneEggPrizes = [32162, // Pizza
        46004, // Phoenix's Feather
        33044, // Hot Sweet Potato
        34122, // Lethe Scroll
        25211, // SPD Ring
        22156, // Gold Swan Helmet
        30065, // Turtle Car Voucher
        30066, // Motorbike Ticket
        33028, // Mighty Grenade
        22166]; // Snow Ears

    /// <summary>The item ids Lucky 7 pays, indexed by prize 0..10.</summary>
    public static readonly ImmutableArray<ushort> Lucky7Prizes = [30063, // Lottery Ticket
        61044, // Light Diamond
        61039, // Death Diamond
        61037, // Blove Diamond
        61035, // Grass Diamond
        61033, // Golden Diamond
        61032, // Pink Diamond
        61031, // Nimbus Diamond
        61030, // Shining Diamond
        61028, // Violet Diamond
        61026]; // Red Diamond

    /// <summary>The item ids the small Lucky 7 pays, indexed by prize 0..10.</summary>
    public static readonly ImmutableArray<ushort> Lucky7MiniPrizes = [32095, // Fugu Hot Pot
        34123, // 8X EXP Potion
        34116, // 6X EXP Potion
        34115, // 5.5X EXP Potion
        34114, // 5X EXP Potion
        34112, // 4X EXP Potion
        34111, // 3.5X EXP Potion
        34110, // 3X EXP Potion
        34109, // 2.5X EXP Potion
        34108, // 2X EXP Potion
        34107]; // 1.5X EXP Potion

    /// <summary>Gets the prize table of a spinning machine (Lucky 7, small Lucky 7, Diamond Hit), indexed by prize 0..10.</summary>
    /// <param name="game">The machine's sport kind.</param>
    /// <returns>The Lucky 7 list, the Diamond Hit list, otherwise the small Lucky 7 list.</returns>
    public static ImmutableArray<ushort> SpinPrizes(byte game) => game switch
    {
        Lucky7 => Lucky7Prizes, // Lucky 7 prize table
        DiamondHit => DiamondHitPrizes, // Diamond Hit prize table
        _ => Lucky7MiniPrizes, // small Lucky 7 prize table
    };

    /// <summary>
    /// Gets every item one play of a machine can pay, so the bag can be checked for all of them before the play is drawn: the whole table
    /// of a machine that draws its prize, the one prize the client's result names on Baby Dolls and Golden Glove.
    /// </summary>
    /// <param name="game">The machine's sport kind.</param>
    /// <param name="detail">The client's detail byte: the caught doll on Baby Dolls, the result on Golden Glove.</param>
    /// <returns>The item ids, one of each play's outcomes.</returns>
    public static IReadOnlyList<ushort> PossiblePrizes(byte game, byte detail) => game switch
    {
        FortuneEgg => FortuneEggPrizes, // Fortune Egg: any egg's prize
        Lucky7 or Lucky7Mini or DiamondHit => SpinPrizes(game), // the spinning machines: their whole table
        Jigsaw or JigsawSec or JigsawThird => JigsawPrizes(game), // the Jigsaw machines: their whole table
        BabyDolls => [DollPrize(detail)], // Baby Dolls: the caught doll's prize
        _ => [GlovePrize(detail)], // Golden Glove and any other machine: the prize of the client's result
    };

    /// <summary>
    /// Gets the shortest time the client's game of a skill machine takes from one result to the next, less a margin for network delay; a
    /// result sent sooner did not come from a play. Baby Dolls: the claw comes down, closes, holds, goes up, holds and drops the doll before
    /// the next grab, about 130 frames of 30 ms (4 s) at the least. Golden Glove: the reward shows for 50 frames and the next play takes at
    /// least one punch of about 14 frames before a quit and three before a result, at least about 1.9 s.
    /// </summary>
    /// <param name="game">The machine's sport kind.</param>
    /// <returns>The shortest time, or null for a machine whose result the server draws.</returns>
    public static TimeSpan? ShortestPlay(byte game) => game switch
    {
        BabyDolls => TimeSpan.FromMilliseconds(2500), // Baby Dolls: 2.5 s, of a cycle of about 4 s
        GoldenGlove => TimeSpan.FromMilliseconds(1500), // Golden Glove: 1.5 s, of at least about 1.9 s
        _ => null, // the server draws the other machines' results
    };

    /// <summary>Gets the item a Baby Dolls play pays.</summary>
    /// <param name="doll">The caught doll's kind (1..7), or 0 for a miss.</param>
    /// <returns>The item id: a Devil's Spar of the doll's rank, or the Lottery Ticket for a miss.</returns>
    public static ushort DollPrize(byte doll) => doll is >= 1 and <= 7
        ? (ushort)(61000 + doll - 1) // Clear Devil's Spar
        : (ushort)30063; // Lottery Ticket

    /// <summary>Gets the item a Golden Glove play pays.</summary>
    /// <param name="result">The client's result byte; 1 is a win.</param>
    /// <returns>The prize item id for a win, otherwise the Lottery Ticket.</returns>
    public static ushort GlovePrize(byte result) => result == 1 ? (ushort)61001 : (ushort)30063; // 61001 Amber Devil's Spar, 30063 Lottery Ticket

    /// <summary>Picks the egg a Fortune Egg play breaks.</summary>
    /// <param name="roll">Random source: <c>roll(n)</c> returns an integer from 0 to n - 1.</param>
    /// <returns>The egg number, 1..10.</returns>
    public static byte EggPrize(Func<int, int> roll) => (byte)(roll(FortuneEggPrizes.Length) + 1);

    /// <summary>Converts a Lucky 7 symbol to the reel stop that shows it.</summary>
    /// <param name="symbol">The symbol, 1..8.</param>
    /// <returns>The reel stop, <c>2 * symbol - 1</c>.</returns>
    public static byte Lucky7Stop(int symbol) => (byte)(2 * symbol - 1);

    /// <summary>The Lucky 7 symbol (8) shown as the starfish.</summary>
    public const int Lucky7Starfish = 8;

    /// <summary>
    /// Draws a result for the Lucky 7, small Lucky 7 and Diamond Hit machines: the prize number and the three reel stops that
    /// show it. Prizes 1..8 show three equal symbols, 9 and 10 end in starfish, and a miss never shows three equal symbols.
    /// </summary>
    /// <param name="roll">Random source: <c>roll(n)</c> returns an integer from 0 to n - 1.</param>
    /// <returns>The prize number (0..10) and the stops of the three reels.</returns>
    public static (byte Prize, byte[] Reels) Spin(Func<int, int> roll)
    {
        var prize = PickPrize(roll);
        int[] symbols = prize switch
        {
            >= 1 and <= 8 => [prize, prize, prize], // prizes 1 to 8: three equal symbols
            9 => [roll(7) + 1, Lucky7Starfish, Lucky7Starfish], // prize 9: two starfish after a random symbol
            10 => [roll(8) + 1, roll(7) + 1, Lucky7Starfish], // prize 10: a starfish after two random symbols
            _ => [roll(8) + 1, roll(8) + 1, roll(7) + 1], // a miss: three random symbols
        };
        if (prize == 0 && symbols[0] == symbols[1] && symbols[1] == symbols[2])
            symbols[2] = symbols[2] % 7 + 1;
        return (prize, [.. symbols.Select(Lucky7Stop)]);
    }
}
