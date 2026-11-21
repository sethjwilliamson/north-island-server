using System.Collections.Concurrent;
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

    /// <summary>
    /// The longest time the packets queued before a close (such as a kick) get to be written before the connection is closed anyway.
    /// </summary>
    private static readonly TimeSpan CloseFlushTime = TimeSpan.FromSeconds(2);

    /// <summary>How often the login and idle timeouts are checked.</summary>
    private static readonly TimeSpan WatchPeriod = TimeSpan.FromSeconds(5);

    /// <summary>The milliseconds in a second.</summary>
    private const long MsPerSecond = 1000;

    /// <summary>The milliseconds in a minute.</summary>
    private const long MsPerMinute = 60 * MsPerSecond;

    /// <summary>The encoded packets waiting to be written to the client, at most <see cref="ServerOptions.OutboxPackets"/>.</summary>
    private readonly Channel<byte[]> _outbox;

    /// <summary>The most packets the outbox holds.</summary>
    private readonly int _outboxPackets;

    /// <summary>The milliseconds a connection has to log in; 0 or less for no limit.</summary>
    private readonly long _loginTimeoutMs;

    /// <summary>The milliseconds a logged-in connection may send nothing; 0 or less for no limit.</summary>
    private readonly long _idleTimeoutMs;

    /// <summary>Cancelled to stop the read and write loops and close the socket, at once or after the flush time of a close.</summary>
    private readonly CancellationTokenSource _stop = new();

    /// <summary>1 once the session is closing: its packets are no longer handled. Read and written with Interlocked.</summary>
    private int _closing;

    /// <summary>When the session connected or latest went back to the login form, in <see cref="Environment.TickCount64"/> milliseconds.</summary>
    private long _loginPhaseSinceMs = Environment.TickCount64;

    /// <summary>
    /// When the latest bytes arrived from the client, in <see cref="Environment.TickCount64"/> milliseconds; read and written with Volatile.
    /// </summary>
    private long _lastReceiveMs = Environment.TickCount64;

    /// <summary>Creates a session for an accepted connection.</summary>
    /// <param name="id">The session id.</param>
    /// <param name="client">The accepted connection.</param>
    /// <param name="router">Dispatches the incoming packets.</param>
    /// <param name="logger">The session's logger.</param>
    /// <param name="options">The server options: packet logging, the outbox size and the login and idle timeouts.</param>
    public GameSession(
        int id,
        TcpClient client,
        PacketRouter router,
        ILogger logger,
        ServerOptions options)
    {
        _client = client;
        _stream = client.GetStream();
        _router = router;
        _logger = logger;
        _logPackets = options.LogPackets;
        _outboxPackets = Math.Max(1, options.OutboxPackets);
        _outbox = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(_outboxPackets) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        _loginTimeoutMs = options.LoginTimeoutSeconds * MsPerSecond;
        _idleTimeoutMs = options.IdleTimeoutMinutes * MsPerMinute;
        Id = id;
        RemoteEndPoint = client.Client.RemoteEndPoint as IPEndPoint;
    }

    /// <summary>True once the session is closing: the router drops its packets and nothing more is sent.</summary>
    public bool Closing => Volatile.Read(ref _closing) != 0;

    /// <summary>1 once the disconnect cleanup began under the session's gate (<see cref="MarkEnded"/>); 0 before.</summary>
    private int _cleaningUp;

    /// <summary>
    /// True once the disconnect cleanup began under the session's gate: the character is saved for the last time and must not change any more,
    /// except by the cleanup itself. A session closed by another session (a kick) is <see cref="Closing"/> but not cleaning up until then.
    /// </summary>
    public bool CleaningUp => Volatile.Read(ref _cleaningUp) != 0;

    /// <summary>
    /// Limits the session's problem log lines: its bad packets (unhandled, malformed, failed, junk skipped) and the refusals that a client can
    /// repeat at will (from the login and item bag handlers); 20 lines per 10 seconds.
    /// </summary>
    public LogBudget ProblemLog { get; } = new(20, 10 * MsPerSecond);

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

    /// <summary>
    /// The map changes (7/x) sent to the player that their client has not yet reported loaded (12/1). While one is outstanding, an event
    /// trigger from the client may have been sent on the map the player left and name a door or NPC of that map. Changed with
    /// Interlocked: a leader's warp counts its team members' map changes from the leader's packet handler.
    /// </summary>
    private int _unloadedMapChanges;

    /// <summary>When the player's latest map change (7/x) was counted, in UTC ticks; read and written with Volatile.</summary>
    private long _mapChangeSentTicks;

    /// <summary>
    /// Counts a map change about to be sent to the player. Called before the character's map is changed, so a trigger that is handled
    /// on another thread and already sees the new map also sees the change outstanding.
    /// </summary>
    /// <param name="nowUtc">The current time.</param>
    public void MapChangeSent(DateTime nowUtc)
    {
        Volatile.Write(ref _mapChangeSentTicks, nowUtc.Ticks);
        Interlocked.Increment(ref _unloadedMapChanges);
    }

    /// <summary>Counts the client's report (12/1) that it loaded a map; extra reports, such as the first map's, are ignored.</summary>
    public void MapChangeLoaded()
    {
        int count;
        do
        {
            count = Volatile.Read(ref _unloadedMapChanges);
            if (count == 0)
                return;
        }
        while (Interlocked.CompareExchange(ref _unloadedMapChanges, count - 1, count) != count);
    }

    /// <summary>
    /// True while a map change sent to the player is not yet loaded by their client, for at most a time so that a 12/1 that never comes
    /// cannot hold the player for good.
    /// </summary>
    /// <param name="nowUtc">The current time.</param>
    /// <param name="maxWait">The longest time a map change counts as outstanding.</param>
    /// <returns>True while the client is still on the map it was moved off.</returns>
    public bool IsLoadingMap(DateTime nowUtc, TimeSpan maxWait) =>
        Volatile.Read(ref _unloadedMapChanges) > 0 && nowUtc.Ticks - Volatile.Read(ref _mapChangeSentTicks) < maxWait.Ticks;

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

    /// <summary>The slot of the pet the player is riding; 0 when none. Kept on the character, so it is saved and still set after a new login.</summary>
    public byte RidingPetSlot
    {
        get => ActiveCharacter?.RidingPetSlot ?? 0;
        set
        {
            if (ActiveCharacter is { } character)
                character.RidingPetSlot = value;
        }
    }

    /// <summary>Called with the attribute, its value and its second value whenever an attribute packet is sent to this session.</summary>
    public Action<byte, int, int>? AttributeSent { get; set; }

    /// <summary>True while auto play is on for the player.</summary>
    public bool AutoPlay { get; set; }

    /// <summary>True from when the owner's client was asked for its saved house song (<c>62/59</c> sub 2) until it answers.</summary>
    public bool HouseSongAsked { get; set; }

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
        if (!_outbox.Writer.TryWrite(FrameCodec.Encode(payload)) && !Closing && Stop(TimeSpan.Zero))
            _logger.LogWarning("[{Session}] {Packets} packets wait for the client, which stopped reading; disconnected", Id, _outboxPackets);
    }

    /// <summary>
    /// Closes the session: its later packets are no longer handled, what is queued (such as a kick) gets a short time to be written, then the
    /// connection is closed whether or not the client read it.
    /// </summary>
    public void Close() => Stop(CloseFlushTime);

    /// <summary>Restarts the time the session has to log in; called when it goes back to the login form.</summary>
    public void RestartLoginTimer() => Volatile.Write(ref _loginPhaseSinceMs, Environment.TickCount64);

    /// <summary>Marks the session closing, ends the outgoing queue and stops the loops and the socket after a time.</summary>
    /// <param name="flush">The time the queued packets get to be written, counted from the first close; zero to close at once.</param>
    /// <returns>True for the call that marked the session closing.</returns>
    private bool Stop(TimeSpan flush)
    {
        var first = Interlocked.Exchange(ref _closing, 1) == 0;
        _outbox.Writer.TryComplete();
        try
        {
            if (flush > TimeSpan.Zero)
            {
                if (first) // a later close must not push the deadline back
                    _stop.CancelAfter(flush);
            }
            else // cancelled on the thread pool: the caller may hold locks, and the cancellation closes the socket
                ThreadPool.QueueUserWorkItem(static stop => Cancel(stop), _stop, preferLocal: false);
        }
        catch (ObjectDisposedException) // the session already ended
        {
        }
        return first;
    }

    /// <summary>Cancels a session's stop token, ignoring a session that already ended.</summary>
    /// <param name="stop">The stop token's source.</param>
    private static void Cancel(CancellationTokenSource stop)
    {
        try
        {
            stop.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Held while one piece of work changes the player's state: each incoming packet, and the timers that change the bag outside packets (a
    /// manufacture finishing, a pet express or forage trip, a fish caught, a quiz prize, the vehicle drain). Without it a timer could change the
    /// bag halfway through a packet handler, so that both pick the same free slot or send their bag packets in another order than they changed it.
    /// </summary>
    private readonly SemaphoreSlim _exclusive = new(1, 1);

    /// <summary>Waits until no other work holds the player's state, then holds it until the result is disposed.</summary>
    /// <returns>The hold; dispose it to let the next work in.</returns>
    public async ValueTask<Exclusive> ExclusiveAsync()
    {
        await _exclusive.WaitAsync().ConfigureAwait(false);
        return new Exclusive(_exclusive);
    }

    /// <summary>Holds the player's state when nothing else does, without waiting; for timers that can try again on their next tick.</summary>
    /// <param name="held">The hold when taken; dispose it to let the next work in.</param>
    /// <returns>True when the hold was taken.</returns>
    public bool TryExclusive(out Exclusive held)
    {
        held = _exclusive.Wait(0) ? new Exclusive(_exclusive) : default;
        return held.Held;
    }

    /// <summary>The longest time <see cref="TryExclusiveFrom"/> waits for another session's gate, in milliseconds.</summary>
    public const int OtherGateWaitMs = 50;

    /// <summary>
    /// Holds this session's state for work that runs under another session's gate and changes this player too: a packet of one player that
    /// changes another player's gold, bag, pets or lists (a trade, a stall buy, a payout), or the second gate of a timer.
    /// <para>
    /// The rule: work that holds one session's gate never waits on <see cref="ExclusiveAsync"/> for another one, since the other session's work
    /// may hold that gate and wait for the first (two players trading at once would wait for each other for ever). It takes the other gate here,
    /// waiting at most <see cref="OtherGateWaitMs"/>, and when that fails it refuses the change with the handler's own "failed" answer and a log
    /// line; the player tries again. A session whose connection ended (<see cref="Closing"/>) is refused as well: its character is saved for the
    /// last time and must not change any more. Check the other player's state (gold, counts, pets) after this returns, not before.
    /// </para>
    /// </summary>
    /// <param name="holder">The session whose gate the caller holds; when it is this session the caller already holds this gate.</param>
    /// <param name="held">The hold when taken (none when <paramref name="holder"/> is this session); dispose it to let the next work in.</param>
    /// <returns>True when this session's state may be changed.</returns>
    public bool TryExclusiveFrom(GameSession holder, out Exclusive held)
    {
        held = default;
        if (ReferenceEquals(holder, this)) // the caller already holds this gate, which is not reentrant
            return true;
        if (!_exclusive.Wait(OtherGateWaitMs))
            return false;
        held = new Exclusive(_exclusive);
        if (!Closing)
            return true;
        held.Dispose();
        held = default;
        return false;
    }

    /// <summary>
    /// Takes this session's gate without waiting, for work that may hold another session's gate or none and cannot tell which (see <see
    /// cref="HandOver"/>).
    /// </summary>
    /// <param name="holder">The session whose gate the caller holds, or null when it holds none or cannot tell.</param>
    /// <param name="held">The hold when taken (none when <paramref name="holder"/> is this session); dispose it to let the next work in.</param>
    /// <returns>
    /// True when the caller may change this session's state now: it holds the gate already (<paramref name="holder"/> is this session, also
    /// during the disconnect cleanup), or the gate was free and the disconnect cleanup has not begun (<see cref="CleaningUp"/>).
    /// </returns>
    public bool TryExclusiveNow(GameSession? holder, out Exclusive held)
    {
        held = default;
        if (ReferenceEquals(holder, this)) // the caller already holds this gate, which is not reentrant
            return true;
        if (!TryExclusive(out held))
            return false;
        if (!CleaningUp)
            return true;
        held.Dispose();
        held = default;
        return false;
    }

    /// <summary>The work handed over by <see cref="HandOver"/> that waits for this session's gate, in the order it was handed over.</summary>
    private readonly ConcurrentQueue<(string What, Action Work)> _handedOver = new();

    /// <summary>
    /// Changes this player's state from work that must not be refused and cannot wait for this session's gate, as it may hold another session's
    /// gate or none (a battle settling: its experience, drops, gold, caught pets and exit for each player). The work runs at once when <see
    /// cref="TryExclusiveNow"/> allows it; otherwise it waits in a queue, in order, for the next holder of the gate: the session's next packet, a
    /// task that holds no gate and waits for it (the rule of <see cref="TryExclusiveFrom"/>), or the disconnect cleanup, which runs it before the
    /// final save (<see cref="MarkEnded"/>). Work that still waits once the character was saved for the last time is dropped with a log line.
    /// </summary>
    /// <param name="holder">The session whose gate the caller holds, or null when it holds none or cannot tell.</param>
    /// <param name="what">What the work does, for the log.</param>
    /// <param name="work">The change, run under this session's gate.</param>
    public void HandOver(GameSession? holder, string what, Action work)
    {
        _handedOver.Enqueue((what, work));
        if (TryExclusiveNow(holder, out var held))
        {
            using (held)
                RunHandedOver();
            return;
        }
        _ = Task.Run(RunHandedOverAsync);
    }

    /// <summary>
    /// Runs the handed-over work, in order; the caller holds this session's gate. A work that throws is logged and the next one runs.
    /// </summary>
    internal void RunHandedOver()
    {
        while (_handedOver.TryDequeue(out var item))
        {
            try
            {
                item.Work();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Session}] handed-over work '{What}' failed", Id, item.What);
            }
        }
    }

    /// <summary>
    /// Waits for this session's gate, holding no other, and runs the handed-over work; drops it with a log line when the disconnect cleanup
    /// ran meanwhile and saved the character for the last time (<see cref="CleaningUp"/>). A session that is only closing is still saved after
    /// this.
    /// </summary>
    private async Task RunHandedOverAsync()
    {
        using (await ExclusiveAsync().ConfigureAwait(false))
        {
            if (!CleaningUp)
            {
                RunHandedOver();
                return;
            }
            while (_handedOver.TryDequeue(out var item))
                _logger.LogWarning("[{Session}] '{What}' not done: the connection ended and the character was saved for the last time", Id,
                    item.What);
        }
    }

    /// <summary>
    /// Marks the session closing once its connection ended, so that later work of other sessions leaves its state alone; called under its gate
    /// before the disconnect cleanup and the final save. The work handed over to it (<see cref="HandOver"/>) runs first, so that it is saved.
    /// </summary>
    internal void MarkEnded()
    {
        RunHandedOver();
        Volatile.Write(ref _cleaningUp, 1);
        Volatile.Write(ref _closing, 1);
    }

    /// <summary>A hold on a session's state, from <see cref="ExclusiveAsync"/> or <see cref="TryExclusive"/>.</summary>
    /// <param name="gate">The session's gate, or null for no hold.</param>
    public readonly struct Exclusive(SemaphoreSlim? gate) : IDisposable
    {
        /// <summary>True when this holds the gate.</summary>
        public bool Held => gate is not null;

        /// <summary>Lets the next work in.</summary>
        public void Dispose() => gate?.Release();
    }

    /// <summary>
    /// Reads and dispatches packets until the client disconnects, the session is closed or the token is cancelled; then gives the queued packets
    /// a short time to be written and closes the socket.
    /// </summary>
    /// <param name="ct">Stops the session.</param>
    public async Task RunAsync(CancellationToken ct)
    {
        _logger.LogInformation("[{Session}] connected from {Remote}", Id, RemoteEndPoint);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        await using var closeSocket = cts.Token.Register(static client => ((TcpClient)client!).Client.Close(), _client).ConfigureAwait(false);
        var writer = WriteLoopAsync(cts.Token);
        var watch = WatchAsync(cts.Token);
        try
        {
            await ReadLoopAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            _outbox.Writer.TryComplete();
            cts.CancelAfter(CloseFlushTime); // a client that stopped reading cannot hold the writer
            await Ended(writer).ConfigureAwait(false);
            await cts.CancelAsync().ConfigureAwait(false);
            await Ended(watch).ConfigureAwait(false);
            _logger.LogInformation("[{Session}] disconnected ({State}, account {Account})", Id, State, Account?.Name ?? "-");
        }
    }

    /// <summary>Waits for a loop of the session to end, ignoring how a closed connection or a cancellation ended it.</summary>
    /// <param name="loop">The loop's task.</param>
    private static async Task Ended(Task loop)
    {
        try
        {
            await loop.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Reads the connection, cuts the bytes into packets and dispatches each one, until the client closes it or the session is closed (the packets
    /// after a close are not handled).
    /// </summary>
    /// <param name="ct">Stops the loop.</param>
    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[8192]; // bytes read per socket read
        while (!Closing)
        {
            var read = await _stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
                return;
            Volatile.Write(ref _lastReceiveMs, Environment.TickCount64);

            var skippedBefore = _decoder.SkippedBytes;
            _decoder.Feed(buffer.AsSpan(0, read));
            while (!Closing && _decoder.NextPayload() is { } payload)
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
            if (_decoder.SkippedBytes != skippedBefore && ProblemLog.TryTake(out var dropped))
                _logger.LogWarning("[{Session}] resync skipped {Bytes} byte(s){Dropped}", Id, _decoder.SkippedBytes - skippedBefore,
                    dropped > 0 ? $" ({dropped} earlier bad-packet line(s) not logged)" : "");
        }
    }

    /// <summary>
    /// Closes the session when it has not logged in within the login timeout (counted from the connection or the latest return to the login
    /// form), or when it is logged in and sent nothing for the idle timeout.
    /// </summary>
    /// <param name="ct">Stops the loop.</param>
    private async Task WatchAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(WatchPeriod);
        while (!Closing && await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            var now = Environment.TickCount64;
            if (State is SessionState.Connected or SessionState.AwaitLogin)
            {
                if (_loginTimeoutMs > 0 && now - Volatile.Read(ref _loginPhaseSinceMs) >= _loginTimeoutMs)
                {
                    _logger.LogInformation("[{Session}] did not log in within {Seconds} s; disconnected", Id, _loginTimeoutMs / MsPerSecond);
                    Close();
                }
            }
            else if (_idleTimeoutMs > 0 && now - Volatile.Read(ref _lastReceiveMs) >= _idleTimeoutMs)
            {
                _logger.LogInformation("[{Session}] sent nothing for {Minutes} min; disconnected", Id, _idleTimeoutMs / MsPerMinute);
                Close();
            }
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
        _stop.Dispose();
        return ValueTask.CompletedTask;
    }
}
