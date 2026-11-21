using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using NorthIsland.Protocol.IO;
using NorthIsland.Protocol.Opcodes;
using NorthIsland.Protocol.Text;

namespace NorthIsland.Server.Features.Houses;

/// <summary>The house blackboard: five message rows that visitors can write and the owner can erase.</summary>
/// <param name="world">The world state, to find the sessions in a house.</param>
/// <param name="tents">The house and furniture handlers, for the house owner and the house entry event.</param>
/// <param name="logger">The logger.</param>
public sealed class BlackboardHandlers(
    WorldState world,
    TentHandlers tents,
    ILogger<BlackboardHandlers> logger)
{
    /// <summary>The number of rows on the board, the longest row text (characters) and the longest writer name (characters).</summary>
    public const int Rows = 5, MaxText = 20, MaxName = 14;

    /// <summary>The item ids of the pieces that act as a board: the Blackboard and the Message Board.</summary>
    public static readonly ImmutableArray<ushort> Boards = [39073, 39109]; // 39073 Message Board, 39109 Notice Board

    /// <summary>Hooks the house entry event so the owner is told about messages on the board.</summary>
    internal void Start()
    {
        tents.HouseEntered += OnHouseEntered;
    }

    /// <summary>Builds the packet <c>72/1</c> that carries all rows of a board.</summary>
    /// <param name="host">The house owner whose board it is.</param>
    /// <returns>The packet.</returns>
    private static byte[] List(Character host)
    {
        var w = new PacketWriter(Action72.Code, Action72.Open);
        lock (host.Blackboard) // visitors of a house whose owner is offline write without a session gate
        {
            foreach (var n in host.Blackboard.OrderBy(n => n.Slot))
            {
                var name = GameEncoding.GetBytes(n.Name);
                var text = GameEncoding.GetBytes(n.Text);
                w.U8(n.Slot).I32(name.Length).Bytes(name).F64(n.Time).I32(text.Length).Bytes(text);
            }
        }
        return w.ToArray();
    }

    /// <summary>Finds the board of the house a session is in.</summary>
    /// <param name="session">The session.</param>
    /// <returns>The house owner, or null when the session is not in a house or the house has no board placed.</returns>
    private TentOwner? HostOf(GameSession session)
    {
        var me = session.ActiveCharacter;
        if (session.State != SessionState.InWorld || me is null || !TentHandlers.IsHouseMap(me.MapId))
            return null;
        var owner = tents.OwnerOfHouse(me.MapId);
        return owner is not null && owner.Character.Furniture.Exists(f => Boards.Contains(f.ItemId) && !f.Retained) ? owner : null;
    }

    /// <summary>Handles <c>72/1</c>: sends the board's rows to a player who clicked the board.</summary>
    /// <param name="session">The session.</param>
    /// <param name="body">The packet body, not used.</param>
    internal ValueTask OnOpen(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (HostOf(session) is { } owner)
            session.Send(List(owner.Character));
        return ValueTask.CompletedTask;
    }

    /// <summary>Handles <c>72/2</c>: writes a row on the board; a row that someone else wrote cannot be written over.</summary>
    /// <param name="session">The writer's session.</param>
    /// <param name="body">The row number (1 to 5), then the text.</param>
    internal ValueTask OnWrite(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (HostOf(session) is not { } owner || body.Length < 2 || session.ActiveCharacter is not { } me)
            return ValueTask.CompletedTask;
        var row = body.Span[0];
        var text = GameEncoding.GetString(body.Span[1..]);
        if (row is < 1 or > Rows || text.Length is 0 or > MaxText || me.Name.Length is 0 or > MaxName)
            return ValueTask.CompletedTask;
        Change(session, owner, "write", host =>
        {
            var note = host.Blackboard.Find(n => n.Slot == row);
            if (note is not null && note.Name != me.Name)
                return false;
            if (note is null)
                host.Blackboard.Add(note = new BoardNote { Slot = row });
            (note.Name, note.Time, note.Text) = (me.Name, GameClock.Now.ToOADate(), text);
            logger.LogInformation("[{Session}] {Name} writes row {Row} on the blackboard", session.Id, me.Name, row);
            return true;
        });
        return ValueTask.CompletedTask;
    }

    /// <summary>Handles <c>72/3</c>: erases a row; the writer and the house owner may do that. The changed board is sent to the house.</summary>
    /// <param name="session">The session.</param>
    /// <param name="body">The row number.</param>
    internal ValueTask OnErase(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (HostOf(session) is not { } owner || body.Length < 1 || session.ActiveCharacter is not { } me)
            return ValueTask.CompletedTask;
        var row = body.Span[0];
        Change(session, owner, "erase", host =>
        {
            var note = host.Blackboard.Find(n => n.Slot == row);
            if (note is null || (note.Name != me.Name && owner.Id != session.Account?.LoginId))
                return false;
            host.Blackboard.Remove(note);
            return true;
        });
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Changes the board of a house under its owner's session gate when the owner is online (and under the board itself, which also keeps two
    /// visitors of an offline owner apart), then sends the changed board to the house; a busy owner session refuses the change.
    /// </summary>
    /// <param name="session">The session that changes the board.</param>
    /// <param name="owner">The house owner.</param>
    /// <param name="what">The change, for the log.</param>
    /// <param name="change">Checks and makes the change on the owner's character; returns false when nothing changed.</param>
    private void Change(GameSession session, TentOwner owner, string what, Func<Character, bool> change)
    {
        var held = default(GameSession.Exclusive);
        if (owner.Session is { } host && !host.TryExclusiveFrom(session, out held))
        {
            logger.LogWarning("[{Session}] blackboard {What} refused: the house owner's session is busy", session.Id, what);
            return;
        }
        using (held)
        {
            bool changed;
            lock (owner.Character.Blackboard)
                changed = change(owner.Character);
            if (changed)
                Broadcast(session, owner.Character);
        }
    }

    /// <summary>Sends the board to everyone in the house of the session, including the session itself.</summary>
    /// <param name="session">The session that changed the board.</param>
    /// <param name="host">The house owner whose board changed.</param>
    private void Broadcast(GameSession session, Character host)
    {
        var packet = List(host);
        foreach (var s in world.OnMap(session.ActiveCharacter!.MapId).Append(session).Distinct())
            s.Send(packet);
    }

    /// <summary>Tells the owner with <c>72/2</c> that there are messages on the board when they enter their house.</summary>
    /// <param name="session">The session that entered the house.</param>
    /// <param name="owner">The house owner's session, or null when the owner is not online.</param>
    private void OnHouseEntered(GameSession session, GameSession? owner)
    {
        if (owner == session && session.ActiveCharacter is { Blackboard.Count: > 0 } c && c.Furniture.Exists(f => Boards.Contains(f.ItemId) && !f.Retained))
            session.Send(new PacketWriter(Action72.Code, 2).U8(1).ToArray());
    }
}
