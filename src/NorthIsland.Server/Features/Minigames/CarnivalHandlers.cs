using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NorthIsland.Protocol.Messages.Events;
using NorthIsland.Protocol.Messages.Items;
using NorthIsland.Protocol.Messages.Player;
using NorthIsland.Protocol.Opcodes;

namespace NorthIsland.Server.Features.Minigames;

/// <summary>Handles the carnival machines, the Lucky Cards and Lucky Wheel games and the item mall's Game 777 tab.</summary>
/// <param name="items">The item data, for names and bag stacking.</param>
/// <param name="options">The mall options (item mall and its Game 777 tab switches).</param>
/// <param name="eventOptions">The event options (the New Year Lucky 7 setting).</param>
/// <param name="calendar">The event calendar, to tell whether the New Year event is on.</param>
/// <param name="logger">The logger.</param>
public sealed class CarnivalHandlers(
    ItemData items,
    IOptions<MallOptions> options,
    IOptions<EventOptions> eventOptions,
    EventCalendar calendar,
    ILogger<CarnivalHandlers> logger)
{
    /// <summary>The single-play machine kinds this handler plays.</summary>
    public static readonly ImmutableArray<byte> Machines = [
        Carnival.FortuneEgg,
        Carnival.BabyDolls,
        Carnival.Lucky7,
        Carnival.GoldenGlove,
        Carnival.Lucky7Mini,
        Carnival.Jigsaw,
        Carnival.JigsawSec,
        Carnival.JigsawThird,
        Carnival.DiamondHit
    ];

    /// <summary>Tells whether this handler plays a game kind.</summary>
    /// <param name="kind">The sport kind.</param>
    /// <returns>True for the carnival machines, Lucky Cards and the Lucky Wheel.</returns>
    public static bool Plays(byte kind) => Machines.Contains(kind) || kind is LuckyCards.Kind or LuckyWheel.Kind;

    /// <summary>Handles the Lucky Wheel's setup packet: the payment choice, a refresh of the wheel, or the double switch.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action: the sub code.</param>
    internal ValueTask OnWheelPacket(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.State != SessionState.InWorld || session.ActiveCharacter is not { } character || body.Length < 1)
            return ValueTask.CompletedTask;
        if (session.Minigame != LuckyWheel.Kind)
        {
            logger.LogWarning("[{Session}] {Name}: Lucky Wheel packet {Sub}, but the wheel is not running", session.Id, character.Name, body.Span[0]);
            return ValueTask.CompletedTask;
        }
        var wheel = session.Minigames.Wheel ??= new WheelRound();
        switch (body.Span[0])
        {
            case Action57.WheelPaidPoints: // 57/12/3: play paid with points, sends a new wheel
            case Action57.WheelPaidTokens: // 57/12/4: play paid with tokens, sends a new wheel
            case Action57.WheelRefresh: // 57/12/1: refresh, sends a new wheel
                if (body.Span[0] != Action57.WheelRefresh)
                    wheel.PayKind = body.Span[0] == Action57.WheelPaidPoints ? (byte)1 : (byte)2;
                wheel.Codes = LuckyWheel.NewWheel(GameRandom.Shared.Next);
                session.Send(CarnivalMessages.WheelList(wheel.Codes));
                break;
            case Action57.WheelDouble: // 57/12/2: double, marks the prize as doubled and confirms
                wheel.Doubled = true;
                session.Send(CarnivalMessages.WheelDoubled(ok: true));
                break;
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Spins the Lucky Wheel: checks that every prize of the wheel would fit in the bag, charges the play, then draws a slot and gives its
    /// prize. The room is checked for every prize before the draw, so a full bag refuses the spin instead of refusing only the prizes that
    /// do not fit, which would let a player spin for free until a wanted prize is the one that fits.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: the pay kind; when empty the kind chosen on the wheel is used.</param>
    internal ValueTask OnWheelSpin(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.State != SessionState.InWorld || session.Account is not { } account || session.ActiveCharacter is not { } character)
            return ValueTask.CompletedTask;
        if (session.Minigame != LuckyWheel.Kind || session.Minigames.Wheel is not { Codes.Length: LuckyWheel.Slots } wheel)
        {
            logger.LogWarning("[{Session}] {Name}: a Lucky Wheel spin without a wheel", session.Id, character.Name);
            return ValueTask.CompletedTask;
        }
        var payKind = body.Length < 1 ? wheel.PayKind : body.Span[0];
        var times = wheel.Doubled ? 2 : 1;
        if (!wheel.Codes.All(code => LuckyWheel.Prize(code) is var (_, prize, n) && Fits(character, prize, n * times)))
        {
            logger.LogInformation("[{Session}] {Name}: no room for every prize of the Lucky Wheel; not spun", session.Id, character.Name);
            session.Send(CarnivalMessages.NoPoints(LuckyWheel.Kind));
            return ValueTask.CompletedTask;
        }
        if (!Pay(session, account, character, LuckyWheel.Kind, payKind, times))
        {
            session.Send(CarnivalMessages.NoPoints(LuckyWheel.Kind));
            return ValueTask.CompletedTask;
        }
        var slot = LuckyWheel.Spin(wheel.Codes, GameRandom.Shared.Next);
        var (_, item, count) = LuckyWheel.Prize(wheel.Codes[slot - 1]);
        var quantity = (byte)(count * times);
        wheel.Doubled = false;
        foreach (var (stack, added) in Bag.Add(character, items, item, quantity))
            session.Send(ItemMessages.BagAdd(new BagEntry { Slot = stack.Slot, ItemId = item, Count = added, Wear = stack.Wear }));
        session.Send(CarnivalMessages.WheelResult(slot, quantity));
        logger.LogInformation("[{Session}] {Name} spins the Lucky Wheel (pay kind {Pay}, x{Times}): slot {Slot}, {Item} x{Count} ({Points} points left)",
            session.Id, character.Name, payKind, times, slot, items[item]?.Name, quantity, account.MallPoints);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Starts a Lucky Cards play: checks that the bag has room for the most a play can pay (the prizes of two pairs, whichever they are,
    /// or the consolation item), charges it and deals the cards. A pair's prize is drawn only after the play is paid, and without the room
    /// check before paying a full bag would lose it.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: the pay kind, 1 when empty.</param>
    internal ValueTask OnPokePlay(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.State != SessionState.InWorld || session.Account is not { } account || session.ActiveCharacter is not { } character)
            return ValueTask.CompletedTask;
        if (session.Minigame != LuckyCards.Kind)
        {
            logger.LogWarning("[{Session}] {Name}: a Lucky Cards play, which is not running", session.Id, character.Name);
            return ValueTask.CompletedTask;
        }
        var payKind = body.Length < 1 ? (byte)1 : body.Span[0];
        if (!LuckyCardsFit(character))
        {
            logger.LogInformation("[{Session}] {Name}: no room for every prize of Lucky Cards; not played", session.Id, character.Name);
            session.Send(CarnivalMessages.NoPoints(LuckyCards.Kind));
            return ValueTask.CompletedTask;
        }
        if (!Pay(session, account, character, LuckyCards.Kind, payKind))
        {
            session.Send(CarnivalMessages.NoPoints(LuckyCards.Kind));
            return ValueTask.CompletedTask;
        }
        session.Minigames.Poke = new PokeRound { Faces = LuckyCards.Deal(GameRandom.Shared.Next) };
        session.Send(CarnivalMessages.PokeStart());
        logger.LogInformation(
            "[{Session}] {Name} plays Lucky Cards (pay kind {Pay}; {Points} points left)",
            session.Id,
            character.Name,
            payKind,
            account.MallPoints);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Handles the Lucky Cards table packet: leaving, flipping a card (a pair pays a prize at once), or finishing the play.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action: the sub code, then the card for a flip.</param>
    internal ValueTask OnPokePacket(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.State != SessionState.InWorld || session.ActiveCharacter is not { } character || body.Length < 1)
            return ValueTask.CompletedTask;
        switch (body.Span[0])
        {
            case Action57.PokeLeft: // 57/9/2: leave, sends the last pair's prize if any and ends the round
                if (session.Minigames.Poke is { Flips: 2 or 3, Pairs: > 0, LastPrize: not 0 } leaving)
                    session.Send(CarnivalMessages.PokeLeavingPrize(leaving.LastPrize));
                session.Minigames.Poke = null;
                break;
            case Action57.PokeFlip when body.Length >= 2 && session.Minigames.Poke is { Flips: < LuckyCards.FlipsPerPlay } round
                && body.Span[1] is >= 1 and <= LuckyCards.Cards: // 57/9/3: flip a card, shows its face and scores a pair on the second flip
                var card = body.Span[1];
                var face = round.Faces[card];
                round.Flips++;
                session.Send(CarnivalMessages.PokeFace(card, face, (byte)round.Flips));
                if (round.Flips % 2 == 1)
                {
                    round.OpenCard = card;
                    break;
                }
                if (round.OpenCard != card && round.Faces[round.OpenCard] == face)
                {
                    round.Pairs++;
                    if (round.Flips < LuckyCards.FlipsPerPlay)
                        round.Faces = LuckyCards.Deal(GameRandom.Shared.Next);
                    round.LastPrize = LuckyCards.PairPrize(GameRandom.Shared.Next);
                    Give(session, character, round.LastPrize, "a Lucky Cards pair");
                }
                break;
            case Action57.PokeDone when session.Minigames.Poke is { Flips: LuckyCards.FlipsPerPlay } done: // 57/9/4: turns done, reveals all the faces
                session.Send(CarnivalMessages.PokeReveal(done.Faces.AsSpan(1)));
                if (done.Pairs == 0)
                    Give(session, character, LuckyCards.Consolation, "Lucky Cards without a pair");
                session.Minigames.Poke = null;
                break;
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>Puts one item into the bag when it fits, and sends the bag change; otherwise only logs that there was no room.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="character">The player's character.</param>
    /// <param name="itemId">The item to give.</param>
    /// <param name="why">What the item is for, used in the log line.</param>
    private void Give(GameSession session, Character character, ushort itemId, string why)
    {
        if (!Fits(character, itemId))
        {
            logger.LogInformation("[{Session}] {Name}: no room for {Item} ({Why})", session.Id, character.Name, items[itemId]?.Name, why);
            return;
        }
        foreach (var (stack, added) in Bag.Add(character, items, itemId, 1))
            session.Send(ItemMessages.BagAdd(new BagEntry { Slot = stack.Slot, ItemId = itemId, Count = added, Wear = stack.Wear }));
        logger.LogInformation("[{Session}] {Name}: {Item} for {Why}", session.Id, character.Name, items[itemId]?.Name, why);
    }

    /// <summary>Starts a game from the item mall's Game 777 tab, or tells the client it is unavailable.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: the game's sport kind.</param>
    internal ValueTask OnMallGame(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.State != SessionState.InWorld || session.ActiveCharacter is not { } character || body.Length < 1)
            return ValueTask.CompletedTask;
        var kind = body.Span[0];
        var mall = options.Value.ItemMall;
        string? refusal = !mall.Enabled || !mall.GameTab ? "the tab is off"
            : !Carnival.MallGames.Contains(kind) ? "not a Game 777 kind"
                : !Plays(kind) ? "not built"
                    : session.Battle is not null ? "in a battle"
                        : session.Event is not null ? "an event is running"
                            : session.Minigame is not null ? $"game {session.Minigame} is running"
                                : null;
        if (refusal is not null)
        {
            logger.LogInformation("[{Session}] {Name}: mall game {Kind} refused ({Why})", session.Id, character.Name, kind, refusal);
            session.Send(CarnivalMessages.Unavailable());
            return ValueTask.CompletedTask;
        }
        session.Minigames.MallMinigame = kind;
        session.Send(EventMessages.SportStart(kind, Carnival.MallGameValue, Carnival.MallGameLevel));
        logger.LogInformation("[{Session}] {Name} starts game {Kind} from the item mall", session.Id, character.Name, kind);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Plays a machine once: checks that every prize the play can pay would fit in the bag, and for the skill machines (Baby Dolls, Golden
    /// Glove) that the result does not come sooner after the last one than the client's game allows; then charges the play, draws the
    /// prize and sends the bag change and the machine's answer. The room is checked for every prize before the draw, so a full bag refuses
    /// the play instead of refusing only the prizes that do not fit, which would let a player play for free until a wanted prize fits.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="game">The machine's sport kind, which has to be the game the session is in.</param>
    /// <param name="body">The packet body: the pay kind (1 when empty or for the free-to-start machines), then a detail byte.</param>
    internal ValueTask OnPlay(GameSession session, byte game, ReadOnlyMemory<byte> body)
    {
        if (session.State != SessionState.InWorld || session.Account is not { } account || session.ActiveCharacter is not { } character)
            return ValueTask.CompletedTask;
        if (session.Minigame != game)
        {
            logger.LogWarning("[{Session}] {Name}: play of machine {Game}, which is not running", session.Id, character.Name, game);
            return ValueTask.CompletedTask;
        }
        var payKind = game is Carnival.Lucky7Mini or Carnival.Jigsaw || body.Length < 1 ? (byte)1 : body.Span[0];
        var detail = body.Length >= 2 ? body.Span[1] : (byte)0;

        if (!Carnival.PossiblePrizes(game, detail).All(p => Fits(character, p)))
        {
            logger.LogInformation("[{Session}] {Name}: no room for every prize of machine {Game}; not played", session.Id, character.Name, game);
            session.Send(CarnivalMessages.NoPoints(game));
            return ValueTask.CompletedTask;
        }
        var now = GameClock.UtcNow;
        var plays = session.Feature<SkillPlays>();
        if (Carnival.ShortestPlay(game) is { } shortest && plays.Last.TryGetValue(game, out var last) && now - last < shortest)
        {
            logger.LogWarning("[{Session}] {Name}: result {Detail} of machine {Game} {Ms} ms after the last one, sooner than a play takes ({Shortest} ms)",
                session.Id, character.Name, detail, game, (int)(now - last).TotalMilliseconds, (int)shortest.TotalMilliseconds);
            session.Send(CarnivalMessages.NoPoints(game));
            return ValueTask.CompletedTask;
        }
        var lucky7 = game is Carnival.Lucky7 or Carnival.Lucky7Mini && calendar.NewYearOn();
        var free = lucky7 && character.NewYearLucky7 >= eventOptions.Value.NewYear.FreeAfter;
        if (!free && !Pay(session, account, character, game, payKind))
        {
            session.Send(CarnivalMessages.NoPoints(game));
            return ValueTask.CompletedTask;
        }
        if (Carnival.ShortestPlay(game) is not null)
            plays.Last[game] = now;
        if (lucky7)
        {
            character.NewYearLucky7 = free ? 0 : character.NewYearLucky7 + 1;
            session.Send(PlayerMessages.Lucky7Count(character.NewYearLucky7));
        }

        Func<int, int> roll = GameRandom.Shared.Next;
        byte[] answer;
        ushort prize;
        switch (game)
        {
            case Carnival.FortuneEgg: // Fortune Egg: draws an egg and its prize
                var egg = Carnival.EggPrize(roll);
                prize = Carnival.FortuneEggPrizes[egg - 1];
                answer = CarnivalMessages.Egg(egg, 1);
                break;
            case Carnival.Lucky7: // Lucky 7: spins the reels, prize from the Lucky 7 table
            case Carnival.Lucky7Mini: // small Lucky 7: same spin, prize from its own table
            case Carnival.DiamondHit: // Diamond Hit: same spin, prize from its own table
                var (slot, reels) = Carnival.Spin(roll);
                prize = Carnival.SpinPrizes(game)[slot];
                answer = CarnivalMessages.Spin(game, slot, reels, 1);
                break;
            case Carnival.Jigsaw: // Jigsaw: draws a piece, prize from its table
            case Carnival.JigsawSec: // second Jigsaw machine: same draw
            case Carnival.JigsawThird: // third Jigsaw machine: same draw
                var (piece, stops) = Carnival.JigsawSpin(roll);
                prize = Carnival.JigsawPrizes(game)[piece];
                answer = CarnivalMessages.Jigsaw(game, piece, stops, 1);
                break;
            case Carnival.BabyDolls: // Baby Dolls: prize from the doll the client caught
                prize = Carnival.DollPrize(detail);
                answer = CarnivalMessages.Reward(game, andExit: false, prize, 1);
                break;
            default: // any other machine: prize from the client's result byte
                prize = Carnival.GlovePrize(detail);
                answer = CarnivalMessages.Reward(game, andExit: detail == 2, prize, 1);
                break;
        }
        foreach (var (stack, added) in Bag.Add(character, items, prize, 1))
            session.Send(ItemMessages.BagAdd(new BagEntry { Slot = stack.Slot, ItemId = prize, Count = added, Wear = stack.Wear }));
        session.Send(answer);
        logger.LogInformation("[{Session}] {Name} plays machine {Game} (pay kind {Pay}): {Prize} ({Points} points left)", session.Id,
            character.Name, game, payKind, items[prize]?.Name, account.MallPoints);
        return ValueTask.CompletedTask;
    }

    /// <summary>When each skill machine (Baby Dolls, Golden Glove) last paid a result to a session, kept on the session.</summary>
    internal sealed class SkillPlays
    {
        /// <summary>The time (UTC) of the last paid result, by the machine's sport kind.</summary>
        public Dictionary<byte, DateTime> Last { get; } = [];
    }

    /// <summary>
    /// Tells whether the most a Lucky Cards play can pay fits in the bag: the consolation item, and the prizes of two pairs for every two
    /// pair prizes (the same one twice included), each tried on a copy of the bag.
    /// </summary>
    /// <param name="character">The player's character.</param>
    /// <returns>True when every outcome fits.</returns>
    private bool LuckyCardsFit(Character character) =>
        Fits(character, LuckyCards.Consolation)
        && Carnival.FortuneEggPrizes.All(a => Carnival.FortuneEggPrizes.All(b => Fits(character, [(a, 1), (b, 1)])));

    /// <summary>Tells whether a prize fits in the character's bag, trying it on a copy of the bag.</summary>
    /// <param name="character">The player's character.</param>
    /// <param name="prize">The prize item id; an unknown item never fits.</param>
    /// <param name="count">How many to add.</param>
    /// <returns>True when all of them fit.</returns>
    private bool Fits(Character character, ushort prize, int count = 1) => Fits(character, [(prize, count)]);

    /// <summary>Tells whether several prizes fit in the character's bag together, adding them one after the other to a copy of the bag.</summary>
    /// <param name="character">The player's character.</param>
    /// <param name="prizes">The prize item ids and counts; an unknown item never fits.</param>
    /// <returns>True when all of them fit.</returns>
    private bool Fits(Character character, IEnumerable<(ushort Item, int Count)> prizes)
    {
        var trial = Bag.Trial(character.Bag);
        return prizes.All(p => items[p.Item] is not null && Bag.Add(trial, items, p.Item, p.Count).Sum(c => c.Added) == p.Count);
    }

    /// <summary>Charges one play (or several) in item-mall points or in tokens taken from the bag.</summary>
    /// <param name="session">The player's session, which gets the token removal.</param>
    /// <param name="account">The player's account, which holds the item-mall points.</param>
    /// <param name="character">The player's character, which holds the tokens.</param>
    /// <param name="game">The machine's sport kind, which sets the price.</param>
    /// <param name="payKind">1 pays points, 2 Carnie Tokens, 3 Fun Tokens.</param>
    /// <param name="times">How many plays are charged.</param>
    /// <returns>True when the player could pay and was charged.</returns>
    private static bool Pay(
        GameSession session,
        Account account,
        Character character,
        byte game,
        byte payKind,
        int times = 1)
    {
        var (points, tokens) = Carnival.Price(game);
        (points, tokens) = (points * times, tokens * times);
        if (payKind == 1)
        {
            if (account.MallPoints < points)
                return false;
            account.MallPoints -= points;
            session.Send(MallMessages.Points(account.MallPoints)); // the machine shows the points it was last sent
            return true;
        }
        var token = payKind == 2 ? Carnival.CarnieToken : payKind == 3 ? Carnival.FunToken : (ushort)0;
        if (token == 0 || tokens == 0 || Bag.Count(character, token) < tokens)
            return false;
        Bag.Remove(character, token, tokens);
        session.Send(ItemMessages.BagRemoveById(token, (byte)tokens));
        return true;
    }
}
