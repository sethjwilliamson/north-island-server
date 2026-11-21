using System.Collections.Immutable;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NorthIsland.Protocol.IO;
using NorthIsland.Protocol.Messages.Events;
using NorthIsland.Protocol.Messages.Player;
using NorthIsland.Protocol.Opcodes;

namespace NorthIsland.Server.Features.Minigames;

/// <summary>
/// Runs Five in a Row in houses: the hall, the 16 tables, the moves, and the antes held while a game is played and paid out at its end.
/// </summary>
/// <param name="world">The world state, for sessions and the leave events.</param>
/// <param name="tents">The house handlers, for house maps and their furniture.</param>
/// <param name="lifetime">The host lifetime, whose stopping event hands back the antes held before the players are saved.</param>
/// <param name="logger">The logger.</param>
public sealed class GobangHandlers(
    WorldState world,
    TentHandlers tents,
    IHostApplicationLifetime lifetime,
    ILogger<GobangHandlers> logger)
{
    /// <summary>Item id of the game console that opens the game.</summary>
    public const ushort ConsoleItem = 39094; // Wire Game Machine

    /// <summary>Item ids of the TVs the console can use.</summary>
    public static readonly ImmutableArray<ushort> TvItems = [39051, 39113]; // 39051 TV, 39113 LCD TV

    /// <summary>The ante in gold for each ante code (0..4) the first player at a table can choose.</summary>
    public static readonly ImmutableArray<int> Antes = [0, 1000, 5000, 10000, 100000];

    /// <summary>The number of tables in a hall, the side of the board in cells, and the most gold a player can hold.</summary>
    public const int Tables = 16, Size = 15, MaxGold = 9_999_999;

    /// <summary>A table: two seats, an ante and a board.</summary>
    private sealed class Table
    {
        /// <summary>The login ids of the two seated players; 0 for an empty seat.</summary>
        public readonly int[] Seat = new int[2];

        /// <summary>The ante code, an index into <see cref="Antes"/>.</summary>
        public byte Ante;

        /// <summary>Whether a game is being played, and whether the last game is over with the players still seated.</summary>
        public bool Playing, Over;

        /// <summary>The side (1 or 2) that moves next.</summary>
        public byte Turn = 1;

        /// <summary>The board cells, row by row (<c>x * Size + y</c>); 0 is empty, otherwise the side that played there.</summary>
        public readonly byte[] Board = new byte[Size * Size];

        /// <summary>The characters whose ante is held for the game being played, by seat; null for a seat that put in nothing.</summary>
        public readonly Character?[] Stakers = new Character?[2];

        /// <summary>The gold in gold coins each staker put in for the game being played; 0 when nothing is held.</summary>
        public int Stake;

        /// <summary>Whether no one sits at the table.</summary>
        public bool Empty => Seat[0] == 0 && Seat[1] == 0;

        /// <summary>Clears the game state (playing, over, turn, board); the seats and the ante stay.</summary>
        public void Reset()
        {
            Playing = Over = false;
            Turn = 1;
            Array.Clear(Board);
        }
    }

    /// <summary>The players in one house map and the tables there.</summary>
    private sealed class Hall
    {
        /// <summary>The login ids of the players in the hall.</summary>
        public readonly HashSet<int> Members = [];

        /// <summary>The tables, numbered 1..16; entry 0 is not used.</summary>
        public readonly Table[] Tables = Enumerable.Range(0, GobangHandlers.Tables + 1).Select(_ => new Table()).ToArray();
    }

    /// <summary>The halls by house map id.</summary>
    private readonly Dictionary<ushort, Hall> _halls = [];

    /// <summary>Guards the halls and their tables.</summary>
    private readonly Lock _lock = new();

    /// <summary>
    /// Subscribes to the world events so that players who leave the world or the map leave their table and hall, and to the server's stop so
    /// that the games being played end in a draw first.
    /// </summary>
    internal void Start()
    {
        world.Leaving += s => Leave(s, quit: true);
        world.MapLeaving += s => Leave(s, quit: true);
        lifetime.ApplicationStopping.Register(DrawAll);
    }

    /// <summary>
    /// Ends every game being played in a draw, which hands each player their ante back. It runs when the server stops, before the sessions
    /// close and their characters are saved: the antes are held only here, so a game still running then would otherwise go to whoever
    /// stays connected longest, and to no one when the save after an autosave is never made.
    /// </summary>
    private void DrawAll()
    {
        lock (_lock)
        {
            foreach (var (map, hall) in _halls)
            {
                for (var t = 1; t <= Tables; t++)
                {
                    if (!hall.Tables[t].Playing)
                        continue;
                    Finish(hall.Tables[t], 0, new byte[5], holder: null); // side 0: a draw, no winning cells
                    logger.LogInformation("Five in a row at table {Table} of house map {Map} ends in a draw: the server is stopping", t, map);
                }
            }
        }
    }

    /// <summary>Builds a Five in a Row packet.</summary>
    /// <param name="option">The sub code that follows the action.</param>
    /// <param name="body">Writes the rest of the packet.</param>
    /// <returns>The packet bytes.</returns>
    private static byte[] Packet(byte option, Action<PacketWriter> body)
    {
        var w = new PacketWriter(Action57.Code, Action57.Gobang).U8(option);
        body(w);
        return w.ToArray();
    }

    /// <summary>Handles the client's TV game choice: when the player stands in a house with the console and a TV set up, it starts the hall.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: the console's furniture index, the TV item id, the TV's furniture index and the game kind.</param>
    internal ValueTask OnTvGame(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.State != SessionState.InWorld || session.ActiveCharacter is not { } c || session.Account is null || body.Length < 5)
            return ValueTask.CompletedTask;
        var (consoleFurn, tvFurn, kind) = (body.Span[0], body.Span[3], body.Span[4]);
        if (kind != 18 || !TentHandlers.IsHouseMap(c.MapId) || c.Level < 10 || session.Battle is not null || session.Stalls.Any
            || tents.OwnerOfHouse(c.MapId) is not { } owner
            || !owner.Character.Furniture.Exists(f => f.Index == consoleFurn && f.ItemId == ConsoleItem && !f.Retained)
            || !owner.Character.Furniture.Exists(f => f.Index == tvFurn && TvItems.Contains(f.ItemId) && !f.Retained))
            return ValueTask.CompletedTask;
        session.Send(EventMessages.SportStart(18, 0, 0));
        return ValueTask.CompletedTask;
    }

    /// <summary>Gets the hall of a house map, creating it on first use.</summary>
    /// <param name="map">The house map id.</param>
    /// <returns>The hall.</returns>
    private Hall HallOf(ushort map)
    {
        if (!_halls.TryGetValue(map, out var hall))
            _halls[map] = hall = new Hall();
        return hall;
    }

    /// <summary>Sends a packet to every player in a hall.</summary>
    /// <param name="hall">The hall.</param>
    /// <param name="packet">The packet.</param>
    /// <param name="except">A login id to leave out; 0 for none.</param>
    private void ToHall(Hall hall, byte[] packet, int except = 0)
    {
        foreach (var id in hall.Members.ToList())
        {
            if (id != except && world.SessionOf(id) is { } s)
                s.Send(packet);
        }
    }

    /// <summary>Sends a packet to a player when they are online.</summary>
    /// <param name="id">The player's login id.</param>
    /// <param name="packet">The packet.</param>
    private void Send(int id, byte[] packet)
    {
        if (world.SessionOf(id) is { } s)
            s.Send(packet);
    }

    /// <summary>
    /// Handles the Five in a Row packet: enter or leave the hall, sit, start the game, or move. A start takes the ante from the other seated
    /// player too, so it first takes that player's session gate (outside <see cref="_lock"/>, which is never held while waiting for a gate) and
    /// is refused when that fails.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action: the sub code, then its arguments.</param>
    internal ValueTask OnPacket(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.State != SessionState.InWorld || session.ActiveCharacter is not { } c || session.Account is null || body.Length < 1)
            return ValueTask.CompletedTask;
        var id = session.Account.LoginId;
        var span = body.Span;
        GameSession? partner = null;
        if (span[0] == Action57.GobangStart)
        {
            lock (_lock)
                partner = Find(c, id) is { Table: { } at, Seat: var seat } && world.SessionOf(at.Seat[1 - seat]) is { } o ? o : null;
        }
        var held = default(GameSession.Exclusive);
        if (partner is not null && !partner.TryExclusiveFrom(session, out held))
        {
            logger.LogWarning("[{Session}] {Name}: five in a row start refused: the other player's session is busy", session.Id, c.Name);
            return ValueTask.CompletedTask;
        }
        using (held)
        {
            lock (_lock)
            {
                switch (span[0])
                {
                    case Action57.GobangEnter when TentHandlers.IsHouseMap(c.MapId): // 57/11/1: enter the hall of this house map
                        Enter(session, c, id);
                        break;
                    case Action57.GobangQuit: // 57/11/2: quit, leaves the table and the hall
                        Leave(session, quit: true);
                        break;
                    case Action57.GobangBack: // 57/11/6: back, leaves the table but stays in the hall
                        Leave(session, quit: false);
                        break;
                    case Action57.GobangSit when span.Length >= 3: // 57/11/3: sit at a table with the ante code asked for
                        Sit(session, c, id, span[1], span[2]);
                        break;
                    case Action57.GobangStart: // 57/11/4: start the game at their table
                        Start(session, c, id, partner);
                        break;
                    case Action57.GobangMove when span.Length >= 2: // 57/11/5: place a stone on a board cell
                        Move(session, c, id, span[1]);
                        break;
                }
            }
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>Adds the player to the hall of their map, sends the member list to the hall and the state of the used tables to the player.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="c">The player's character.</param>
    /// <param name="id">The player's login id.</param>
    private void Enter(GameSession session, Character c, int id)
    {
        var hall = HallOf(c.MapId);
        hall.Members.Add(id);
        var members = hall.Members.ToList();
        var list = Packet(Action57.GobangPlayers, w => { w.U16((ushort)members.Count); foreach (var m in members) w.I32(m); });
        ToHall(hall, list);
        for (var t = 1; t <= Tables; t++)
        {
            var table = hall.Tables[t];
            if (table.Empty)
                continue;
            session.Send(Packet(Action57.GobangAnte, w => w.U8((byte)t).U8(table.Ante)));
            for (var s = 0; s < 2; s++)
            {
                if (table.Seat[s] != 0)
                    session.Send(Packet(Action57.GobangSeat, w => w.U8((byte)t).U8((byte)(s + 1)).I32(table.Seat[s])));
            }
            if (table.Playing)
                session.Send(Packet(Action57.GobangStarted, w => w.U8((byte)t)));
        }
    }

    /// <summary>Finds the table a player sits at.</summary>
    /// <param name="c">The player's character, which gives the map.</param>
    /// <param name="id">The player's login id.</param>
    /// <returns>The hall (null when the map has none), the table (null when the player sits at none), its number and the seat index 0..1.</returns>
    private (Hall? Hall, Table? Table, int Index, int Seat) Find(Character c, int id)
    {
        if (!_halls.TryGetValue(c.MapId, out var hall))
            return (null, null, 0, -1);
        for (var t = 1; t <= Tables; t++)
        {
            var s = Array.IndexOf(hall.Tables[t].Seat, id);
            if (s >= 0)
                return (hall, hall.Tables[t], t, s);
        }
        return (hall, null, 0, -1);
    }

    /// <summary>Seats the player at a table when a seat is free and they can afford the ante; the first sitter sets the ante.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="c">The player's character.</param>
    /// <param name="id">The player's login id.</param>
    /// <param name="index">The table number, 1..16.</param>
    /// <param name="anteCode">The ante code the player asks for; only used when they are the first to sit.</param>
    private void Sit(
        GameSession session,
        Character c,
        int id,
        byte index,
        byte anteCode)
    {
        if (!_halls.TryGetValue(c.MapId, out var hall) || !hall.Members.Contains(id) || index is < 1 or > Tables || Find(c, id).Table is not null)
            return;
        var table = hall.Tables[index];
        if (table.Playing || table.Over)
            return;
        var free = table.Seat[0] == 0 ? 0 : table.Seat[1] == 0 ? 1 : -1;
        if (free < 0)
            return;
        var first = table.Empty;
        var ante = first ? anteCode : table.Ante;
        if (ante >= Antes.Length || c.Gold < Antes[ante] || c.Gold + Antes[ante] > MaxGold)
            return;
        table.Seat[free] = id;
        if (first)
        {
            table.Ante = ante;
            ToHall(hall, Packet(Action57.GobangAnte, w => w.U8(index).U8(ante)));
        }
        ToHall(hall, Packet(Action57.GobangSeat, w => w.U8(index).U8((byte)(free + 1)).I32(id)));
        logger.LogInformation("[{Session}] {Name} sits at five-in-a-row table {Table} seat {Seat}", session.Id, c.Name, index, free + 1);
    }

    /// <summary>
    /// Starts the game at the player's table when both seats are taken by players who can pay the ante, and takes the ante from both: it is
    /// held until the game ends, so neither can throw or bank the gold they might lose.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="c">The player's character.</param>
    /// <param name="id">The player's login id.</param>
    /// <param name="partner">The other seated player's session, whose gate the caller holds; null when none was seated.</param>
    private void Start(GameSession session, Character c, int id, GameSession? partner)
    {
        var (hall, table, index, seat) = Find(c, id);
        if (hall is null || table is null || table.Playing || table.Seat[0] == 0 || table.Seat[1] == 0)
            return;
        var amount = Antes[table.Ante];
        var players = table.Seat.Select(s => world.SessionOf(s) is { ActiveCharacter: { } pc } o ? (Session: o, Character: pc) : default).ToArray();
        if (!ReferenceEquals(players[1 - seat].Session, partner)) // the other seat changed hands since its gate was taken
        {
            logger.LogWarning("[{Session}] {Name}: five in a row start at table {Table} refused: the other seat changed", session.Id, c.Name, index);
            return;
        }
        if (players.Any(p => p.Character is null || p.Character.Gold < amount))
        {
            logger.LogWarning("[{Session}] {Name}: five in a row start at table {Table}, but a player cannot pay the ante of {Ante}", session.Id,
                c.Name, index, amount);
            return;
        }
        table.Reset();
        table.Playing = true;
        table.Stake = amount;
        for (var s = 0; s < 2 && amount > 0; s++)
        {
            var (o, pc) = players[s];
            pc.Gold -= amount;
            table.Stakers[s] = pc;
            o.Send(PlayerMessages.Money(pc.Gold));
        }
        ToHall(hall, Packet(Action57.GobangStarted, w => w.U8((byte)index)));
        logger.LogInformation("Five in a row starts at table {Table} (ante {Ante} held from each player)", index, amount);
    }

    /// <summary>Places a stone for the player when it is their turn, and ends the game on five in a row or a full board.</summary>
    /// <param name="session">The player's session, whose gate the caller holds.</param>
    /// <param name="c">The player's character.</param>
    /// <param name="id">The player's login id.</param>
    /// <param name="cell">The board cell, 0..224.</param>
    private void Move(GameSession session, Character c, int id, byte cell)
    {
        var (hall, table, index, seat) = Find(c, id);
        if (hall is null || table is null || !table.Playing || cell >= Size * Size || table.Board[cell] != 0 || table.Turn != seat + 1)
            return;
        var side = (byte)(seat + 1);
        table.Board[cell] = side;
        foreach (var s in table.Seat)
            Send(s, Packet(Action57.GobangPlay, w => w.U8(1).U8(side).U8(cell)));
        if (Line(table.Board, cell) is { } five)
        {
            Finish(table, side, five, session);
            return;
        }
        if (table.Board.All(b => b != 0))
        {
            Finish(table, 0, new byte[5], session);
            return;
        }
        table.Turn = (byte)(3 - side);
    }

    /// <summary>Looks for five or more stones of the same side in a line through a cell.</summary>
    /// <param name="board">The board cells.</param>
    /// <param name="cell">The cell of the stone just placed.</param>
    /// <returns>Five cells of the line, lowest first, or null when there is no line of five.</returns>
    public static byte[]? Line(byte[] board, int cell)
    {
        int x = cell / Size, y = cell % Size;
        var side = board[cell];
        foreach (var (dx, dy) in new[] { (1, 0), (0, 1), (1, 1), (1, -1) })
        {
            var cells = new List<int> { cell };
            for (var dir = -1; dir <= 1; dir += 2)
            {
                for (var k = 1; ; k++)
                {
                    int nx = x + dx * k * dir, ny = y + dy * k * dir;
                    if (nx is < 0 or >= Size || ny is < 0 or >= Size || board[nx * Size + ny] != side)
                        break;
                    cells.Add(nx * Size + ny);
                }
            }
            if (cells.Count >= 5)
                return cells.OrderBy(v => v).Take(5).Select(v => (byte)v).ToArray();
        }
        return null;
    }

    /// <summary>Ends the game: tells the seated players the result and pays out the antes held for it.</summary>
    /// <param name="table">The table.</param>
    /// <param name="winner">The winning side, 1 or 2; 0 for a draw.</param>
    /// <param name="five">The five winning cells; zeros when there are none.</param>
    /// <param name="holder">The session whose gate the caller holds, or null; see <see cref="Pay"/>.</param>
    /// <param name="leaver">The login id of a player who left in the middle of the game and gets no result; 0 for none.</param>
    private void Finish(Table table, byte winner, byte[] five, GameSession? holder, int leaver = 0)
    {
        table.Playing = false;
        table.Over = true;
        foreach (var s in table.Seat.Where(s => s != leaver))
            Send(s, Packet(Action57.GobangPlay, w => { w.U8(2).U8(winner); foreach (var cell in five) w.U8(cell); }));
        Settle(table, winner, holder);
    }

    /// <summary>
    /// Pays out the antes held for the game at a table: the winner gets both, up to the most gold a player can hold; on a draw, or when the
    /// winner put in nothing, each staker gets their own ante back. Clears what the table holds.
    /// </summary>
    /// <param name="table">The table, whose seats still name the two players.</param>
    /// <param name="winner">The winning side, 1 or 2; 0 for a draw.</param>
    /// <param name="holder">The session whose gate the caller holds, or null; see <see cref="Pay"/>.</param>
    private void Settle(Table table, byte winner, GameSession? holder)
    {
        var (stake, stakers) = (table.Stake, table.Stakers.ToArray());
        table.Stake = 0;
        Array.Clear(table.Stakers);
        if (stake == 0)
            return;
        if (winner != 0 && stakers[winner - 1] is { } wc)
        {
            var pot = stake * stakers.Count(s => s is not null);
            Pay(table.Seat[winner - 1], wc, pot, holder, $"wins at five in a row from {stakers[2 - winner]?.Name}");
            return;
        }
        for (var s = 0; s < 2; s++)
        {
            if (stakers[s] is { } sc)
                Pay(table.Seat[s], sc, stake, holder, "gets the ante back from five in a row");
        }
    }

    /// <summary>
    /// Adds gold to a character, up to the most a player can hold, and sends the new amount when they are online with that character. An online
    /// player is paid under their session gate (<see cref="GameSession.HandOver"/>): at once when the caller holds it or it is free; otherwise
    /// before their next packet or their final save, never waiting here, as the caller holds <see cref="_lock"/>.
    /// </summary>
    /// <param name="id">The player's login id.</param>
    /// <param name="c">The character that gets the gold.</param>
    /// <param name="amount">The gold to add.</param>
    /// <param name="holder">The session whose gate the caller holds, or null when it holds none that matters here.</param>
    /// <param name="why">What the gold is for, for the log.</param>
    private void Pay(int id, Character c, int amount, GameSession? holder, string why)
    {
        if (world.SessionOf(id) is { } s && ReferenceEquals(s.ActiveCharacter, c))
            s.HandOver(holder, $"five in a row: {amount} gold", () => Credit(s, c, amount, why));
        else
            Credit(null, c, amount, why);
    }

    /// <summary>Adds the gold, up to the most a player can hold, tells the player when they are online and logs it.</summary>
    /// <param name="s">The player's session, whose gate the caller holds; null when the player is offline.</param>
    /// <param name="c">The character that gets the gold.</param>
    /// <param name="amount">The gold to add.</param>
    /// <param name="why">What the gold is for, for the log.</param>
    private void Credit(GameSession? s, Character c, int amount, string why)
    {
        var paid = Math.Max(0, Math.Min(amount, MaxGold - c.Gold));
        c.Gold += paid;
        if (s is not null && !s.Closing)
            s.Send(PlayerMessages.Money(c.Gold));
        logger.LogInformation("{Name} {Why}: {Paid} of {Amount} gold", c.Name, why, paid, amount);
    }

    /// <summary>Takes the player out of their seat, which loses a game in progress, and out of the hall when they quit.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="quit">True to leave the hall as well; false to leave only the table.</param>
    private void Leave(GameSession session, bool quit)
    {
        if (session.ActiveCharacter is not { } c || session.Account is null)
            return;
        var id = session.Account.LoginId;
        lock (_lock)
        {
            var (hall, table, index, seat) = Find(c, id);
            if (hall is null)
                return;
            if (table is not null)
            {
                var other = table.Seat[1 - seat];
                if (table.Playing && other != 0)
                    Finish(table, (byte)(2 - seat), new byte[5], holder: null, leaver: id);
                else if (table.Over && other != 0)
                    Send(other, Packet(Action57.GobangPlay, w => w.U8(3)));
                table.Seat[seat] = 0;
                table.Reset();
                ToHall(hall, Packet(Action57.GobangSeatLeft, w => w.U8((byte)index).U8((byte)(seat + 1))));
                if (table.Empty)
                {
                    table.Ante = 0;
                    ToHall(hall, Packet(Action57.GobangAnte, w => w.U8((byte)index).U8(0)));
                }
            }
            if (quit && hall.Members.Remove(id))
            {
                var members = hall.Members.ToList();
                ToHall(hall, Packet(Action57.GobangPlayers, w => { w.U16((ushort)members.Count); foreach (var m in members) w.I32(m); }));
            }
        }
    }
}
