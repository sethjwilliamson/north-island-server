using Microsoft.Extensions.Logging;
using NorthIsland.Protocol;
using NorthIsland.Protocol.IO;

namespace NorthIsland.Server.Net;

/// <summary>Maps action and sub codes to packet handlers and sends the packets that free the client when a handler fails or stays silent.</summary>
/// <param name="logger">The router's logger.</param>
public sealed class PacketRouter(
    ILogger<PacketRouter> logger)
{
    /// <summary>The handlers registered for an action and sub code, with their names.</summary>
    private readonly Dictionary<(byte Action, byte Sub), (PacketHandler Handler, string Name)> _bySub = [];

    /// <summary>The handlers registered for a whole action, with their names.</summary>
    private readonly Dictionary<byte, (PacketHandler Handler, string Name)> _byAction = [];

    /// <summary>Every registered route; the sub code is null for a route of a whole action.</summary>
    public IEnumerable<(byte Action, byte? Sub, string Name)> Routes =>
        _bySub.Select(kv => (kv.Key.Action, (byte?)kv.Key.Sub, kv.Value.Name)).Concat(_byAction.Select(kv => (kv.Key, (byte?)null, kv.Value.Name)));

    /// <summary>Registers a handler for an action and sub code.</summary>
    /// <param name="action">The action code.</param>
    /// <param name="sub">The sub code.</param>
    /// <param name="name">The route's name, for the log.</param>
    /// <param name="handler">The handler.</param>
    /// <returns>The router.</returns>
    public PacketRouter On(byte action, byte sub, string name, PacketHandler handler)
    {
        _bySub.Add((action, sub), (handler, name));
        return this;
    }

    /// <summary>Registers a handler for a whole action, used when no route has the sub code.</summary>
    /// <param name="action">The action code.</param>
    /// <param name="name">The route's name, for the log.</param>
    /// <param name="handler">The handler.</param>
    /// <returns>The router.</returns>
    public PacketRouter On(byte action, string name, PacketHandler handler)
    {
        _byAction.Add(action, (handler, name));
        return this;
    }

    /// <summary>The packets to send when a handler fails or stays silent.</summary>
    private readonly Dictionary<(byte Action, byte? Sub), (Func<IEnumerable<byte[]>> Clear, bool WhenSilent)> _clears = [];

    /// <summary>Every registered clear route.</summary>
    public IEnumerable<(byte Action, byte? Sub, Func<IEnumerable<byte[]>> Clear, bool WhenSilent)> ClearRoutes =>
        _clears.Select(kv => (kv.Key.Action, kv.Key.Sub, kv.Value.Clear, kv.Value.WhenSilent));

    /// <summary>Registers the packets that free the client when the handler of an action and sub code fails.</summary>
    /// <param name="action">The action code.</param>
    /// <param name="sub">The sub code; null for any sub code of the action.</param>
    /// <param name="clear">Builds the packets to send.</param>
    /// <param name="whenSilent">True to send them also when the handler sent nothing.</param>
    /// <returns>The router.</returns>
    public PacketRouter Clears(byte action, byte? sub, Func<IEnumerable<byte[]>> clear, bool whenSilent)
    {
        _clears[(action, sub)] = (clear, whenSilent);
        return this;
    }

    /// <summary>Sends the clear packets of a route, if it has any.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="action">The action code.</param>
    /// <param name="sub">The sub code.</param>
    /// <param name="name">The route's name, for the log.</param>
    /// <param name="why">The reason, for the log.</param>
    private void SendClear(
        GameSession session,
        byte action,
        byte sub,
        string name,
        string why)
    {
        if (!_clears.TryGetValue((action, sub), out var c) && !_clears.TryGetValue((action, null), out c))
            return;
        logger.LogInformation("[{Session}] {Name}: {Why}, sending the packet that frees the client", session.Id, name, why);
        foreach (var p in c.Clear())
            session.Send(p);
    }

    /// <summary>Finds the handler of a packet and runs it, sending the clear packets when it fails, is malformed or stays silent.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="payload">The packet: action, sub code and body.</param>
    public async ValueTask DispatchAsync(GameSession session, byte[] payload)
    {
        if (payload.Length == 0)
        {
            logger.LogWarning("[{Session}] empty payload", session.Id);
            return;
        }

        var action = payload[0];
        (PacketHandler Handler, string Name) route;
        ReadOnlyMemory<byte> body;
        if (payload.Length >= 2 && _bySub.TryGetValue((action, payload[1]), out route)) // Sub
            body = payload.AsMemory(2);
        else if (_byAction.TryGetValue(action, out route))
            body = payload.AsMemory(1);
        else
        {
            logger.LogWarning("[{Session}] unhandled {Action}/{Sub} in state {State} | {Hex}",
                session.Id, action, payload.Length >= 2 ? payload[1].ToString(System.Globalization.CultureInfo.InvariantCulture) : "-", // Sub
                session.State, Hex.Format(payload));
            return;
        }

        var sub = payload.Length >= 2 ? payload[1] : (byte)0;
        using var exclusive = await session.ExclusiveAsync().ConfigureAwait(false); // timers that change the bag wait for the packet to be handled
        var sent = session.BeginDispatch();
        try
        {
            await route.Handler(session, body).ConfigureAwait(false);
            if (sent.Sent == 0 && _clears.TryGetValue((action, sub), out var c) && c.WhenSilent
                && session.State == SessionState.InWorld)
                SendClear(session, action, sub, route.Name, "no reply");
        }
        catch (PacketFormatException ex)
        {
            logger.LogWarning("[{Session}] malformed {Name}: {Message} | {Hex}", session.Id, route.Name, ex.Message, Hex.Format(payload));
            SendClear(session, action, sub, route.Name, "malformed");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "[{Session}] handler {Name} failed | {Hex}", session.Id, route.Name, Hex.Format(payload));
            SendClear(session, action, sub, route.Name, "failed");
        }
    }
}
