using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using NorthIsland.Protocol.Messages.Items;

namespace NorthIsland.Server.Features.Travel;

/// <summary>
/// Serves the Magic Magnet: a player wearing it asks another player wearing it to be pulled to them, and the other player agrees or refuses.
/// </summary>
/// <param name="world">The online sessions.</param>
/// <param name="travel">The teleport rule, for the maps and states that bar a pull.</param>
/// <param name="logger">The logger.</param>
public sealed class MagnetHandlers(
    WorldState world,
    TravelRules travel,
    ILogger<MagnetHandlers> logger)
{
    /// <summary>The item id of the Magic Magnet.</summary>
    public const ushort MagicMagnet = 34021; // Magic Magnet

    /// <summary>The lowest total level at which the magnet can be used, for the asker and the asked.</summary>
    public const int MinLevel = 50;

    /// <summary>Tells whether the character wears the magnet in equipment slot 6.</summary>
    /// <param name="c">The character.</param>
    /// <returns>True when they wear it.</returns>
    private static bool Wears(Character c) => c.Equipment.Exists(e => e.Slot == 6 && e.ItemId == MagicMagnet);

    /// <summary>Gives the total level of a character.</summary>
    /// <param name="c">The character.</param>
    /// <returns>The level.</returns>
    private static int Level(Character c) => Career.TotalLevel(c);

    /// <summary>
    /// Tells whether the player cannot pull or be pulled: a busy state of the teleport rule, or a map no teleport may leave or reach; the
    /// asker's map is where the pull lands, so it may not be the sea either (the client's sceneBarsUse and sceneBarsAnswer).
    /// </summary>
    /// <param name="s">The session.</param>
    /// <param name="c">The session's character.</param>
    /// <param name="asker">True for the asker, whose map the asked player is pulled to.</param>
    /// <returns>True when barred.</returns>
    private bool Barred(GameSession s, Character c, bool asker) =>
        TravelRules.BusyRefusal(s) is not null || (asker ? travel.ArrivalRefusal(c.MapId) is not null : travel.IsBarred(c.MapId));

    /// <summary>Handles the magnet packet (<c>23/90</c>): sub code 1 asks a player, 2 agrees, 3 refuses and 4 answers with a code.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: u8 sub code and its arguments.</param>
    internal ValueTask OnMagnet(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.State != SessionState.InWorld || session.ActiveCharacter is not { } c || session.Account is null || body.Length < 1)
            return ValueTask.CompletedTask;
        var myId = session.Account.LoginId;
        switch (body.Span[0]) // op
        {
            case 1 when body.Length >= 6: // 23/90/1: ask another player to be pulled
                Ask(session, c, myId, body.Span[1], BinaryPrimitives.ReadInt32LittleEndian(body.Span[2..])); // slot
                break;
            case 2: // 23/90/2: the asked player agrees
                Agree(session, c);
                break;
            case 3: // 23/90/3: the asked player refuses (code 255)
                Answer(session, 255);
                break;
            case 4 when body.Length >= 2: // 23/90/4: the asked player answers with a reason code
                Answer(session, body.Span[1]); // reason
                break;
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Asks another player to be pulled. The first failing condition on either side is sent back as a result code; otherwise both sides are marked
    /// and the other player is asked.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="c">The asker's character.</param>
    /// <param name="myId">The asker's login id.</param>
    /// <param name="slot">The slot byte of the request, echoed to the asker.</param>
    /// <param name="targetId">The login id of the player asked.</param>
    private void Ask(
        GameSession session,
        Character c,
        int myId,
        byte slot,
        int targetId)
    {
        if (!Wears(c))
            return;
        int code = Level(c) < MinLevel ? 15
            : session.Battle is not null ? 7
                : session.Stalls.Any ? 12
                    : session.Trades.Items is not null ? 11
                        : session.Vehicle is not null ? 13
                            : session.Magnet.Target != 0 ? 3
                                : Barred(session, c, asker: true) ? 9
                                    : targetId == myId || world.SessionOf(targetId) is not { } target || target.ActiveCharacter is not { } t ? 254
                                        : !Wears(t) ? 21
                                            : Level(t) < MinLevel ? 16
                                                : target.Battle is not null ? 8
                                                    : target.Stalls.Any ? 2
                                                        : target.Trades.Items is not null ? 1
                                                            : target.Vehicle is not null ? 14
                                                                : target.Magnet.Requester != 0 || target.Magnet.Target != 0 ? 6
                                                                    : Barred(target, t, asker: false) ? 10
                                                                        : 0;
        if (code != 0)
        {
            logger.LogInformation("[{Session}] {Name}: magnet toward {Target} refused ({Code})", session.Id, c.Name, targetId, code);
            session.Send(MagnetMessages.Result((byte)code));
            return;
        }
        var asked = world.SessionOf(targetId)!;
        session.Magnet.Target = targetId;
        asked!.Magnet.Requester = myId;
        asked.Send(MagnetMessages.Request(myId));
        session.Send(MagnetMessages.Waiting(targetId, slot));
    }

    /// <summary>
    /// The asked player agrees: if both sides still can (no vehicle, both wear the magnet, neither is barred), they are moved to the asker's
    /// position and the asker is told it succeeded.
    /// </summary>
    /// <param name="target">The session of the asked player.</param>
    /// <param name="t">The character of the asked player.</param>
    private void Agree(GameSession target, Character t)
    {
        var requesterId = target.Magnet.Requester;
        if (requesterId == 0 || world.SessionOf(requesterId) is not { } requester || requester.ActiveCharacter is not { } c)
        {
            target.Magnet.Requester = 0;
            return;
        }
        target.Magnet.Requester = 0;
        requester.Magnet.Target = 0;
        if (requester.Vehicle is not null || target.Vehicle is not null || !Wears(c) || !Wears(t) || Barred(requester, c, asker: true)
            || Barred(target, t, asker: false))
        {
            requester.Send(MagnetMessages.Result(21));
            return;
        }
        logger.LogInformation("[{Session}] {Name} is magnetised to {Requester} on map {Map}", target.Id, t.Name, c.Name, c.MapId);
        requester.Send(MagnetMessages.Result(20));
        world.ChangeMap(target, new DoorWarp(c.MapId, c.X, c.Y, 0));
    }

    /// <summary>The asked player answers with a result code, which is passed to the asker.</summary>
    /// <param name="target">The session of the asked player.</param>
    /// <param name="code">The result code for the asker.</param>
    private void Answer(GameSession target, byte code)
    {
        var requesterId = target.Magnet.Requester;
        target.Magnet.Requester = 0;
        if (requesterId != 0 && world.SessionOf(requesterId) is { } requester)
        {
            requester.Magnet.Target = 0;
            requester.Send(MagnetMessages.Result(code));
        }
    }
}
