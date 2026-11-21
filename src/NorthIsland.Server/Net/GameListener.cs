using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NorthIsland.Server.Net;

/// <summary>
/// Background service that accepts game connections and runs a session for each one, saving the account when it ends. A connection over the
/// global cap or the cap of its address is closed at once.
/// </summary>
/// <param name="router">Dispatches the incoming packets of each session.</param>
/// <param name="store">The account store.</param>
/// <param name="world">The online sessions.</param>
/// <param name="accountSessions">The live session of each account, released when a session ends.</param>
/// <param name="options">The server options.</param>
/// <param name="loggers">Creates the loggers of the listener and its sessions.</param>
public sealed class GameListener(
    PacketRouter router,
    IAccountStore store,
    WorldState world,
    AccountSessions accountSessions,
    IOptions<ServerOptions> options,
    ILoggerFactory loggers) : BackgroundService
{
    /// <summary>The server options.</summary>
    private readonly ServerOptions _options = options.Value;

    /// <summary>The listener's logger.</summary>
    private readonly ILogger _logger = loggers.CreateLogger<GameListener>();

    /// <summary>The running session tasks, by session id.</summary>
    private readonly ConcurrentDictionary<int, Task> _sessions = new();

    /// <summary>The id given to the latest session.</summary>
    private int _lastSessionId;

    /// <summary>The open game connections, by client address.</summary>
    private readonly ConcurrentDictionary<IPAddress, int> _byAddress = new();

    /// <summary>The open game connections; changed with Interlocked.</summary>
    private int _open;

    /// <summary>The connections refused since the latest refusal that was logged.</summary>
    private int _refusedUnlogged;

    /// <summary>When a refusal was latest logged, in <see cref="Environment.TickCount64"/> milliseconds.</summary>
    private long _refusalLoggedMs = long.MinValue / 2; // long ago

    /// <summary>The milliseconds between two log lines about refused connections.</summary>
    private const long RefusalLogMs = 10_000;

    /// <summary>Completed with the local endpoint once the listener is listening.</summary>
    private readonly TaskCompletionSource<IPEndPoint> _bound = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes with the local endpoint once the listener is listening.</summary>
    public Task<IPEndPoint> Bound => _bound.Task;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var listener = new TcpListener(IPAddress.Parse(_options.BindAddress), _options.GamePort);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        _bound.TrySetResult(endpoint);
        _logger.LogInformation("Game server listening on {Endpoint}", endpoint);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (SocketException ex) when (!stoppingToken.IsCancellationRequested)
                {
                    await AcceptFailedAsync(_logger, ex, stoppingToken).ConfigureAwait(false);
                    continue;
                }
                var address = AddressOf(client);
                if (!TryAdmit(address))
                {
                    Refused(address);
                    client.Dispose();
                    continue;
                }
                client.NoDelay = true;
                var session = new GameSession(
                    Interlocked.Increment(ref _lastSessionId),
                    client,
                    router,
                    loggers.CreateLogger<GameSession>(),
                    _options);
                _sessions[session.Id] = RunSessionAsync(session, address, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            listener.Stop();
            await Task.WhenAll(_sessions.Values).ConfigureAwait(false);
        }
    }

    /// <summary>Handles a failed accept: a client that left while queued is only logged, anything else is warned about and the loop waits 100 ms.</summary>
    /// <param name="logger">The logger to use.</param>
    /// <param name="ex">The socket error.</param>
    /// <param name="ct">Cancels the wait.</param>
    internal static async Task AcceptFailedAsync(ILogger logger, SocketException ex, CancellationToken ct)
    {
        if (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted)
        {
            logger.LogDebug("Accept: client went away while queued ({Error})", ex.SocketErrorCode);
            return;
        }
        logger.LogWarning(ex, "Accept failed ({Error}); still listening", ex.SocketErrorCode);
        await Task.Delay(TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);
    }

    /// <summary>Gives the client address of a connection, an IPv4 address mapped to IPv6 as IPv4.</summary>
    /// <param name="client">The connection.</param>
    /// <returns>The address; <see cref="IPAddress.None"/> when it is not known.</returns>
    private static IPAddress AddressOf(TcpClient client)
    {
        var address = (client.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;
        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }

    /// <summary>Counts a new connection when it stays within the global cap and the cap of its address (loopback addresses have none).</summary>
    /// <param name="address">The client address.</param>
    /// <returns>True when the connection is counted and may run.</returns>
    private bool TryAdmit(IPAddress address)
    {
        if (Interlocked.Increment(ref _open) > _options.MaxConnections && _options.MaxConnections > 0)
        {
            Interlocked.Decrement(ref _open);
            return false;
        }
        var count = _byAddress.AddOrUpdate(address, 1, static (_, n) => n + 1);
        if (count > _options.MaxConnectionsPerAddress && _options.MaxConnectionsPerAddress > 0 && !IPAddress.IsLoopback(address))
        {
            Leave(address);
            return false;
        }
        return true;
    }

    /// <summary>Uncounts a connection that ended or was refused at its address and in the global count.</summary>
    /// <param name="address">The client address.</param>
    private void Leave(IPAddress address)
    {
        Interlocked.Decrement(ref _open);
        while (_byAddress.TryGetValue(address, out var n))
        {
            if (n <= 1 ? _byAddress.TryRemove(new KeyValuePair<IPAddress, int>(address, n)) : _byAddress.TryUpdate(address, n - 1, n))
                return;
        }
    }

    /// <summary>Logs a refused connection, at most one line per 10 seconds with the number refused meanwhile.</summary>
    /// <param name="address">The client address.</param>
    private void Refused(IPAddress address)
    {
        var unlogged = Interlocked.Increment(ref _refusedUnlogged);
        var now = Environment.TickCount64;
        if (now - _refusalLoggedMs < RefusalLogMs)
            return;
        _refusalLoggedMs = now;
        Interlocked.Exchange(ref _refusedUnlogged, 0);
        _logger.LogWarning(
            "Connection from {Address} refused: {Open} connection(s) open (cap {Max}, {PerAddress} per address); {Count} refused since the last line",
            address, Volatile.Read(ref _open), _options.MaxConnections, _options.MaxConnectionsPerAddress, unlogged);
    }

    /// <summary>
    /// Runs a session and, when it ends, aborts its trades, closes its stalls, removes it from the world, saves its account and releases its claim
    /// on the account and its place in the connection counts; each step runs even when an earlier one throws. The cleanup and the save run under
    /// the session's gate, which no packet of its own holds any more: a timer or another player's packet that changes the character finishes
    /// first, and the session is marked ended so that none changes it after the save (<see cref="GameSession.TryExclusiveFrom"/>).
    /// </summary>
    /// <param name="session">The session to run.</param>
    /// <param name="address">The client address it is counted under.</param>
    /// <param name="ct">Cancels the session.</param>
    private async Task RunSessionAsync(GameSession session, IPAddress address, CancellationToken ct)
    {
        await using (session.ConfigureAwait(false))
        {
            try
            {
                await session.RunAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Session}] session crashed", session.Id);
            }
        }

        var exclusive = await session.ExclusiveAsync().ConfigureAwait(false); // no packet of its own runs now: waits for timers and other players
        try
        {
            session.MarkEnded();
            Cleanup(session, "trade abort", () => TradeHandlers.Abort(session));
            Cleanup(session, "pet trade abort", () => PetTradeHandlers.Abort(session));
            Cleanup(session, "stall close", () =>
            {
                session.Stalls.Items = null;
                session.Stalls.Pets = null;
                session.Stalls.Utensils = null;
            });
            Cleanup(session, "world leave", () => world.Leave(session));
        }
        finally
        {
            if (session.Account is { } account && session.ActiveCharacter is { } character)
            {
                try
                {
                    await store.SaveAsync(account, CancellationToken.None).ConfigureAwait(false);
                    _logger.LogInformation("[{Session}] saved {Name} at map {Map} ({X}, {Y})",
                        session.Id, character.Name, character.MapId, character.X, character.Y);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[{Session}] saving {Account} failed", session.Id, account.Name);
                }
            }
            exclusive.Dispose();
            accountSessions.Release(session);
            Leave(address);
            _sessions.TryRemove(session.Id, out _);
        }
    }

    /// <summary>Runs one disconnect cleanup step, logging instead of throwing when it fails so that the later steps and the save still run.</summary>
    /// <param name="session">The session that ended.</param>
    /// <param name="step">The step, for the log.</param>
    /// <param name="action">The step itself.</param>
    private void Cleanup(GameSession session, string step, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Session}] disconnect cleanup '{Step}' failed", session.Id, step);
        }
    }
}
