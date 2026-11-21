using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using NorthIsland.Protocol.Messages.Events;
using NorthIsland.Protocol.Opcodes;

namespace NorthIsland.Server.Features.Events;

/// <summary>Handles the action 20 packets: the event triggers, the request for the next result and the dialog answer.</summary>
/// <param name="engine">The event engine.</param>
/// <param name="world">The world state.</param>
/// <param name="logger">The log.</param>
public sealed class EventHandlers(
    EventEngine engine,
    NorthIsland.Server.World.WorldState world,
    ILogger<EventHandlers> logger)
{
    /// <summary>
    /// Hooks the engine into the world so a pending role change is applied and the running event ends when a player leaves their map, and
    /// keeps the place a player came to Carnie from for the Carnie exit's warp.
    /// </summary>
    internal void Start()
    {
        world.MapLeaving += engine.ApplyPendingRole;
        world.MapChanging += CarnieReturn.Remember;
        world.MapChanging += engine.LeaveMap;
    }

    /// <summary>Passes the result of a minigame to the engine.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: the result byte, 0 when lost.</param>
    internal ValueTask OnMinigameResult(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.State == SessionState.InWorld && session.ActiveCharacter is not null)
            engine.MinigameEnded(session, body.Length > 0 ? body.Span[0] : 0); // result
        return ValueTask.CompletedTask;
    }

    /// <summary>Handles an action 20 packet: the next result request, a dialog answer, or a trigger with its NPC or door index.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: the sub code, then its argument.</param>
    internal ValueTask OnEvent(GameSession session, ReadOnlyMemory<byte> body)
    {
        var span = body.Span;
        if (span.Length == 0 || session.State != SessionState.InWorld || session.ActiveCharacter is null)
            return ValueTask.CompletedTask;

        var sub = span[0];
        switch (sub)
        {
            case Action20.RequestNext: // 20/6: the client asks for the next step of the script
                engine.Next(session);
                break;
            case Action20.DialogAnswer: // 20/9: the player answers a dialog
                if (span.Length > 1)
                    engine.Answer(session, span[1]);
                else
                    logger.LogWarning("[{Session}] 20/9 without an answer code", session.Id);
                break;
            default: // any other sub code: starts the event of that sub code
                var index = span.Length >= 3 ? BinaryPrimitives.ReadUInt16LittleEndian(span[1..3]) : 0;
                if (NorthIsland.Server.World.WorldState.IsLoadingMap(session))
                {
                    // sent on the map the player was just warped off (a team member following its leader through a door): its index
                    // names a door or NPC of that map, not of this one; the client still waits for the event's end
                    logger.LogInformation("[{Session}] trigger {Trigger} index {Index} sent before map {Map} loaded, dropped",
                        session.Id, sub, index, session.ActiveCharacter.MapId);
                    session.Send(EventMessages.Finished());
                    break;
                }
                engine.Start(session, sub, index);
                break;
        }
        return ValueTask.CompletedTask;
    }
}
