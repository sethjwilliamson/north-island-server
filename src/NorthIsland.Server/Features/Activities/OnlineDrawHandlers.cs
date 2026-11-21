using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NorthIsland.Protocol;
using NorthIsland.Protocol.IO;
using NorthIsland.Protocol.Messages.Items;

namespace NorthIsland.Server.Features.Activities;

/// <summary>Handles the online draw: sends the prize list and the daily draw count, and rolls a prize when the player draws.</summary>
/// <param name="items">The item records, used to drop unknown prizes and to fit them in the bag.</param>
/// <param name="world">The world state, used to announce top prizes.</param>
/// <param name="options">The activity options giving the draw settings.</param>
/// <param name="logger">Logger for the draw messages.</param>
public sealed class OnlineDrawHandlers(
    ItemData items,
    WorldState world,
    IOptions<ActivityOptions> options,
    ILogger<OnlineDrawHandlers> logger)
{
    /// <summary>The number of draws a character may make per day.</summary>
    public const int DrawsPerDay = 3;

    /// <summary>The most prizes the list holds.</summary>
    public const int MaxPrizes = 20;

    /// <summary>The time source; replaced in tests.</summary>
    public Func<DateTime> Clock { get; set; } = () => GameClock.Now;

    /// <summary>The packet <c>104/1</c> with the prize list and the draws used today.</summary>
    /// <param name="used">The draws used today.</param>
    /// <param name="prizes">The prizes in list order.</param>
    /// <returns>The packet.</returns>
    public static byte[] Info(int used, IReadOnlyList<(ushort Item, byte Count)> prizes)
    {
        var w = new PacketWriter(104, 1).U8(1).U8((byte)used).U8((byte)prizes.Count);
        foreach (var (item, count) in prizes)
            w.U16(item).U8(count);
        return w.ToArray();
    }

    /// <summary>The packet <c>104/1</c> with the result of a draw.</summary>
    /// <param name="place">The 1-based place of the won prize in the list.</param>
    /// <param name="used">The draws used today, including this one.</param>
    /// <returns>The packet.</returns>
    public static byte[] Result(int place, int used) => new PacketWriter(104, 1).U8(2).U8((byte)place).U8((byte)used).ToArray();

    /// <summary>The prizes in force as item and count, in list order.</summary>
    /// <returns>The prize list.</returns>
    public IReadOnlyList<(ushort Item, byte Count)> Prizes() => [.. Rows().Select(p => (p.Item, (byte)Math.Clamp((int)p.Count, 1, 255)))];

    /// <summary>The prize rows in force.</summary>
    /// <returns>The rows.</returns>
    private List<OnlineDrawPrize> Rows() =>
        [
            .. (options.Value.OnlineDraw.Prizes.Count > 0
                ? options.Value.OnlineDraw.Prizes
                : OnlineDrawOptions.WikiPrizes).Where(p => p.Item > 0 && items[p.Item] is not null)
                .Take(MaxPrizes)
        ];

    /// <summary>Picks the index of a prize row at random by the rows' weights, or with equal chance when all weights are 0.</summary>
    /// <param name="rows">The prize rows.</param>
    /// <param name="rng">The random source.</param>
    /// <returns>The index into <paramref name="rows"/>.</returns>
    public static int Pick(IReadOnlyList<OnlineDrawPrize> rows, Random rng)
    {
        var total = rows.Sum(r => (long)Math.Max(0, r.Weight));
        if (total <= 0)
            return rng.Next(rows.Count);
        var roll = rng.NextInt64(total);
        for (var i = 0; i < rows.Count; i++)
        {
            roll -= Math.Max(0, rows[i].Weight);
            if (roll < 0)
                return i;
        }
        return rows.Count - 1;
    }

    /// <summary>The packet <c>2/15</c> that announces a top prize in the chat ticker.</summary>
    /// <param name="item">The prize item id.</param>
    /// <param name="count">The prize count.</param>
    /// <param name="name">The winner's character name.</param>
    /// <returns>The packet.</returns>
    public static byte[] Announcement(ushort item, byte count, string name) => new PacketWriter(2, 15).U8(4).U16(item).U8(count).RawString(name).ToArray();

    /// <summary>The day number of a time, counted from the configured reset hour instead of midnight.</summary>
    /// <param name="time">The time.</param>
    /// <returns>The day number.</returns>
    public int DayOf(DateTime time) => DateOnly.FromDateTime(time.AddHours(-Math.Clamp(options.Value.OnlineDraw.ResetHour, 0, 23))).DayNumber;

    /// <summary>The draws a character has used today; resets the counter first when a new day has begun.</summary>
    /// <param name="character">The character.</param>
    /// <returns>The number of draws used today.</returns>
    private int Used(Character character)
    {
        var today = DayOf(Clock());
        if (character.OnlineDrawDay != today)
        {
            character.OnlineDrawDay = today;
            character.OnlineDrawsUsed = 0;
        }
        return character.OnlineDrawsUsed;
    }

    /// <summary>Sends the prize list and the draws used to a player who has just entered the world, when the draw is on and has prizes.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="character">The player's character.</param>
    public void SendOnLogin(GameSession session, Character character)
    {
        if (options.Value.OnlineDraw.Enabled && Prizes() is { Count: > 0 } prizes)
            session.Send(Info(Used(character), prizes));
    }

    /// <summary>
    /// Handles a draw request: refuses it when the daily draws are used up or the bag has no room for any one of the prizes, otherwise rolls a
    /// prize, puts it in the bag, answers with the place and announces a top prize to the 2014 clients online. The room is checked for every
    /// prize before the roll: refusing only a rolled prize that does not fit, without using a draw, would let a player draw again and again
    /// until a wanted prize is the one that fits.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body, not read.</param>
    internal ValueTask OnDraw(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.State != SessionState.InWorld || session.ActiveCharacter is not { } c)
            return ValueTask.CompletedTask;
        var rows = options.Value.OnlineDraw.Enabled ? Rows() : [];
        var prizes = Prizes();
        if (rows.Count == 0)
        {
            logger.LogInformation("[{Session}] {Name}: online draw requested, nothing to draw (off or no prizes configured)", session.Id, c.Name);
            session.Send(Info(DrawsPerDay, []));
            return ValueTask.CompletedTask;
        }
        var used = Used(c);
        var room = prizes.All(p => Bag.Add(Bag.Trial(c.Bag), items, p.Item, p.Count).Sum(x => x.Added) == p.Count);
        if (used >= DrawsPerDay || !room)
        {
            logger.LogInformation("[{Session}] {Name}: online draw refused ({Used} used, room in the bag for every prize: {Room})", session.Id, c.Name, used,
                room);
            session.Send(Info(used, prizes));
            return ValueTask.CompletedTask;
        }
        var place = Pick(rows, GameRandom.Shared);
        var pick = prizes[place];
        c.OnlineDrawsUsed = (byte)(used + 1);
        foreach (var (stack, added) in Bag.Add(c, items, pick.Item, pick.Count))
            session.Send(ItemMessages.BagAdd(new BagEntry { Slot = stack.Slot, ItemId = pick.Item, Count = added, Wear = stack.Wear }));
        session.Send(Result(place + 1, c.OnlineDrawsUsed));
        if (rows[place].Top)
        {
            var line = Announcement(pick.Item, pick.Count, c.Name);
            foreach (var other in world.OnlineSessions().Where(s => s.Client == ClientGeneration.Client2014).ToList())
                other.Send(line);
        }
        logger.LogInformation(
            "[{Session}] {Name} draws online: place {Place} ({Item} x{Count}), {Used} of {Max} used",
            session.Id,
            c.Name,
            place + 1,
            items[pick.Item]?.Name,
            pick.Count,
            c.OnlineDrawsUsed,
            DrawsPerDay);
        return ValueTask.CompletedTask;
    }
}
