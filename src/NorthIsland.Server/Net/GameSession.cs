using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NorthIsland.Protocol;
using NorthIsland.Protocol.Framing;
using NorthIsland.Protocol.Opcodes;

namespace NorthIsland.Server.Net;

/// <summary>One client connection: reads and dispatches its packets, queues what is sent back and holds the player's per-connection state.</summary>
public sealed class GameSession : IAsyncDisposable
{
    /// <summary>The TCP connection.</summary>
    private readonly TcpClient _client;

    /// <summary>The connection's network stream.</summary>
    private readonly NetworkStream _stream;

    /// <summary>Dispatches the incoming packets.</summary>
    private readonly PacketRouter _router;

    /// <summary>The session's logger.</summary>
    private readonly ILogger _logger;

    /// <summary>True to log every packet with its bytes.</summary>
    private readonly bool _logPackets;

    /// <summary>Cuts the received bytes into packets.</summary>
    private readonly FrameDecoder _decoder = new();

    /// <summary>The encoded packets waiting to be written to the client.</summary>
    private readonly Channel<byte[]> _outbox = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });

    /// <summary>Creates a session for an accepted connection.</summary>
    /// <param name="id">The session id.</param>
    /// <param name="client">The accepted connection.</param>
    /// <param name="router">Dispatches the incoming packets.</param>
    /// <param name="logger">The session's logger.</param>
    /// <param name="logPackets">True to log every packet.</param>
    public GameSession(
        int id,
        TcpClient client,
        PacketRouter router,
        ILogger logger,
        bool logPackets)
    {
        _client = client;
        _stream = client.GetStream();
        _router = router;
        _logger = logger;
        _logPackets = logPackets;
        Id = id;
        RemoteEndPoint = client.Client.RemoteEndPoint as IPEndPoint;
    }

    /// <summary>The session id, unique while the server runs.</summary>
    public int Id { get; }

    /// <summary>The client's address, null when it is not known.</summary>
    public IPEndPoint? RemoteEndPoint { get; }

    /// <summary>How far the session has got: from connected up to in the world.</summary>
    public SessionState State { get; set; } = SessionState.Connected;

    /// <summary>The client edition, set from the version in the login packet.</summary>
    public ClientGeneration Client { get; set; } = ClientGeneration.Client2010;

    /// <summary>The account that logged in; null before login.</summary>
    public Account? Account { get; set; }

    /// <summary>The character being played; null outside the world.</summary>
    public Character? ActiveCharacter { get; set; }

    /// <summary>The event script the player is running, if any.</summary>
    public EventRun? Event { get; set; }

    /// <summary>The state of the map's NPCs for this player, by NPC role, as set by event scripts.</summary>
    public Dictionary<ushort, byte> NpcStates { get; } = [];

    /// <summary>
    /// The shows and hides of the map's NPCs that event scripts sent to this player since the map was entered, by NPC role: true for a show,
    /// with the record's seconds. A move inside the map makes the client load it again and forget them, so they are sent again then.
    /// </summary>
    public Dictionary<ushort, (bool Shown, int Seconds)> NpcVisibility { get; } = [];

    /// <summary>True while the player is in GM admin mode.</summary>
    public bool AdminMode { get; set; }

    /// <summary>The battle the player is fighting in, if any.</summary>
    public Battle? Battle { get; set; }

    /// <summary>True when the player joined a running battle and the others on their map were told they left the scene.</summary>
    public bool HiddenInBattle { get; set; }

    /// <summary>True once the player has started to leave the game.</summary>
    public bool LeavingGame { get; set; }

    /// <summary>The battle the player is watching, if any.</summary>
    public Battle? Watching { get; set; }

    /// <summary>The direction of the player's latest walk; null before they walk.</summary>
    public byte? Facing { get; set; }

    /// <summary>The room of the house the player is in.</summary>
    public byte HouseRoom { get; set; }

    /// <summary>The NPCs (map, role) hidden from this player, with the time until which they stay hidden.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<(ushort Map, ushort Role), DateTime> HiddenNpcs { get; } = new();

    /// <summary>When the player latest arrived in the world or on a map.</summary>
    public DateTime ArrivedUtc { get; set; }

    /// <summary>The NPCs (map, role) that may not touch this player again, with the time until which they cannot.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<(ushort Map, ushort Role), DateTime> TouchCooldown { get; } = new();

    /// <summary>The pick-up objects (map, role) the player has emptied, with the time they refill.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<(ushort Map, ushort Role), DateTime> EmptiedObjects { get; } = new();

    /// <summary>The start of the online time not yet added to the account; null when the player is not online.</summary>
    public DateTime? OnlineSinceUtc { get; set; }

    /// <summary>The vehicle the player has placed or is riding; null when none.</summary>
    public PlacedVehicle? Vehicle { get; set; }

    /// <summary>True while the player's auto-collect is running.</summary>
    public bool AutoCollecting { get; set; }

    /// <summary>The bag slot of the pet the player is riding; 0 when none.</summary>
    public byte RidingPetSlot { get; set; }

    /// <summary>Called with the attribute, its value and its second value whenever an attribute packet is sent to this session.</summary>
    public Action<byte, int, int>? AttributeSent { get; set; }

    /// <summary>True while auto play is on for the player.</summary>
    public bool AutoPlay { get; set; }

    /// <summary>Gives the per-session state object of a feature, creating it on first use.</summary>
    /// <typeparam name="T">The state class; it needs a parameterless constructor.</typeparam>
    /// <returns>The state object of this session.</returns>
    public T Feature<T>() where T : class, new() => (T)_features.GetOrAdd(typeof(T), static _ => new T());

    /// <summary>The state objects of the features, by type.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Type, object> _features = new();

    /// <summary>The packet counter of the packet being handled in the current async flow.</summary>
    private static readonly AsyncLocal<DispatchCount?> CurrentDispatch = new();

    /// <summary>Starts counting the packets sent while one incoming packet is handled.</summary>
    /// <returns>The counter.</returns>
    public DispatchCount BeginDispatch()
    {
        var count = new DispatchCount(this);
        CurrentDispatch.Value = count;
        return count;
    }

    /// <summary>Queues a packet for the client, counting it and logging it when asked.</summary>
    /// <param name="payload">The packet: action, sub code and body.</param>
    public void Send(byte[] payload)
    {
        if (CurrentDispatch.Value is { } d && d.Session == this)
            d.Sent++;
        if (AttributeSent is { } sent && payload is [Action8.Code, Action8.SetAttribute, var attr, var sign, ..] && payload.Length >= 12)
            sent(attr, sign == 2 ? -BitConverter.ToInt32(payload, 4) : BitConverter.ToInt32(payload, 4), // 4 value, 4 value
                BitConverter.ToInt32(payload, 8)); // 8 value2
        if (_logPackets)
            _logger.LogInformation("[{Session}] S->C {Code} len={Length} | {Hex}", Id, CodeOf(payload, fromClient: false), payload.Length, Hex.Format(payload));
        _outbox.Writer.TryWrite(FrameCodec.Encode(payload));
    }

    /// <summary>Ends the outgoing queue; the writer then drains it and shuts the socket down.</summary>
    public void Close() => _outbox.Writer.TryComplete();

    /// <summary>Reads and dispatches packets until the client disconnects or the token is cancelled.</summary>
    /// <param name="ct">Stops the session.</param>
    public async Task RunAsync(CancellationToken ct)
    {
        _logger.LogInformation("[{Session}] connected from {Remote}", Id, RemoteEndPoint);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var writer = WriteLoopAsync(cts.Token);
        try
        {
            await ReadLoopAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
        {
        }
        finally
        {
            _outbox.Writer.TryComplete();
            await cts.CancelAsync().ConfigureAwait(false);
            try
            {
                await writer.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
            }
            _logger.LogInformation("[{Session}] disconnected ({State}, account {Account})", Id, State, Account?.Name ?? "-");
        }
    }

    /// <summary>Reads the connection, cuts the bytes into packets and dispatches each one, until the client closes it.</summary>
    /// <param name="ct">Stops the loop.</param>
    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        while (true)
        {
            var read = await _stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
                return;

            var skippedBefore = _decoder.SkippedBytes;
            _decoder.Feed(buffer.AsSpan(0, read));
            while (_decoder.NextPayload() is { } payload)
            {
                if (_logPackets)
                    _logger.LogInformation(
                        "[{Session}] C->S {Code} len={Length} | {Hex}",
                        Id,
                        CodeOf(payload, fromClient: true),
                        payload.Length,
                        Hex.Format(payload));
                await _router.DispatchAsync(this, payload).ConfigureAwait(false);
            }
            if (_decoder.SkippedBytes != skippedBefore)
                _logger.LogWarning("[{Session}] resync skipped {Bytes} byte(s)", Id, _decoder.SkippedBytes - skippedBefore);
        }
    }

    /// <summary>Writes the queued packets to the client until the queue ends, then shuts the socket down.</summary>
    /// <param name="ct">Stops the loop.</param>
    private async Task WriteLoopAsync(CancellationToken ct)
    {
        await foreach (var frame in _outbox.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            await _stream.WriteAsync(frame, ct).ConfigureAwait(false);

        _client.Client.Shutdown(SocketShutdown.Both);
    }

    /// <summary>Gives the packet code for the log: the action, with the sub code when that action has one.</summary>
    /// <param name="payload">The packet.</param>
    /// <param name="fromClient">True for a packet from the client.</param>
    /// <returns>The code text.</returns>
    private static string CodeOf(byte[] payload, bool fromClient) => payload.Length switch
    {
        0 => "(empty)", // no bytes
        1 => $"{payload[0]}", // only the action
        _ when !SubCodes.HasSub(payload[0], fromClient) => $"{payload[0]}", // action without a sub code: only the action
        _ => $"{payload[0]}/{payload[1]}", // action and sub code
    };

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}
