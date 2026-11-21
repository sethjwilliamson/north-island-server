using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Options;

namespace NorthIsland.Server.Features.Login;

/// <summary>
/// Slows down password guessing and mass registration. Too many failed logins of one account name from one address lock that name out for that
/// address only, so that nobody can lock an account out for its owner; too many failed logins from one address, of any names, lock the address
/// out. Across all addresses, an account name that failed too often only makes each of its logins wait <see cref="NameBrakeMs"/> before the
/// password is checked, which slows a guesser on many addresses but never refuses a login. One address may auto-register only a few accounts
/// per hour. Loopback addresses have no address lockout and no registration limit (the server's own test tools), but their failures still lock
/// the name out for the loopback address and count for the name's brake.
/// </summary>
/// <param name="options">The server options: the failures before a lockout, the lockout time and the registrations per hour.</param>
public sealed class LoginThrottle(
    IOptions<ServerOptions> options)
{
    /// <summary>The milliseconds in a second.</summary>
    private const long MsPerSecond = 1000;

    /// <summary>The milliseconds of the registration window: one hour.</summary>
    private const long RegistrationWindowMs = 3600 * MsPerSecond;

    /// <summary>The calls between two sweeps of the entries whose window and lockout are over.</summary>
    private const int SweepEvery = 1024;

    /// <summary>The milliseconds each login of an account name waits once the name has failed too often from all addresses together.</summary>
    public const int NameBrakeMs = 1000; // one second: barely felt by the owner, a tenfold slowdown of a guesser's tries

    /// <summary>The server options, read once at construction.</summary>
    private readonly ServerOptions _options = options.Value;

    /// <summary>The failed logins by account name (lower case) and client address; the unknown address counts as <see cref="IPAddress.None"/>.</summary>
    private readonly ConcurrentDictionary<(string Name, IPAddress Address), Strikes> _byNameAndAddress = new();

    /// <summary>The failed logins by account name from all addresses together, ignoring case; they only brake, never lock out.</summary>
    private readonly ConcurrentDictionary<string, Strikes> _byName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The failed logins by client address.</summary>
    private readonly ConcurrentDictionary<IPAddress, Strikes> _byAddress = new();

    /// <summary>The auto-registrations by client address.</summary>
    private readonly ConcurrentDictionary<IPAddress, Strikes> _registrations = new();

    /// <summary>The calls since the latest sweep; changed with Interlocked.</summary>
    private int _sinceSweep;

    /// <summary>The milliseconds of the failure window and of a lockout.</summary>
    private long LockoutMs => Math.Max(1, _options.LoginLockoutSeconds) * MsPerSecond;

    /// <summary>Gives the seconds left of a lockout of the account name for the address, or of the address.</summary>
    /// <param name="name">The account name.</param>
    /// <param name="address">The client address, or null when it is not known.</param>
    /// <returns>The seconds until logins are let in again; 0 when they are not locked out.</returns>
    public int LockedSeconds(string name, IPAddress? address)
    {
        var now = Environment.TickCount64;
        var left = Math.Max(LeftMs(_byNameAndAddress, Key(name, address), now), Limited(address) is { } a ? LeftMs(_byAddress, a, now) : 0);
        return (int)((left + MsPerSecond - 1) / MsPerSecond); // rounded up to whole seconds
    }

    /// <summary>Gives how long a login of an account name waits before its password is checked.</summary>
    /// <param name="name">The account name.</param>
    /// <returns><see cref="NameBrakeMs"/> once the name has had the failures before a lockout from all addresses in the window; otherwise 0.</returns>
    public int BrakeMs(string name) =>
        _byName.TryGetValue(name, out var strikes) && strikes.InWindow(Environment.TickCount64, LockoutMs) >= FailureLimit ? NameBrakeMs : 0;

    /// <summary>
    /// Counts a failed login of an account name from an address: locks the name out for the address, or the address out, once either reaches the
    /// failures before a lockout, and counts the failure for the name's brake.
    /// </summary>
    /// <param name="name">The account name.</param>
    /// <param name="address">The client address, or null when it is not known.</param>
    public void Failed(string name, IPAddress? address)
    {
        var now = Environment.TickCount64;
        _byNameAndAddress.GetOrAdd(Key(name, address), static _ => new Strikes()).Count(now, LockoutMs, FailureLimit, LockoutMs);
        _byName.GetOrAdd(name, static _ => new Strikes()).Add(now, LockoutMs);
        if (Limited(address) is { } a)
            _byAddress.GetOrAdd(a, static _ => new Strikes()).Count(now, LockoutMs, FailureLimit, LockoutMs);
        SweepNow(now);
    }

    /// <summary>
    /// Forgets the failed logins of an account name from an address after a successful login from there; the failures from other addresses
    /// still count for them and for the name's brake.
    /// </summary>
    /// <param name="name">The account name.</param>
    /// <param name="address">The client address, or null when it is not known.</param>
    public void Succeeded(string name, IPAddress? address) => _byNameAndAddress.TryRemove(Key(name, address), out _);

    /// <summary>The failures that start a lockout, at least 1.</summary>
    private int FailureLimit => Math.Max(1, _options.LoginFailuresBeforeLockout);

    /// <summary>Gives the key of an account name and an address in <see cref="_byNameAndAddress"/>.</summary>
    /// <param name="name">The account name.</param>
    /// <param name="address">The client address, or null when it is not known.</param>
    /// <returns>The name in lower case and the address, IPv4 when it is mapped to IPv6, or <see cref="IPAddress.None"/> when it is not known.</returns>
    private static (string Name, IPAddress Address) Key(string name, IPAddress? address) =>
        (name.ToLowerInvariant(), address is null ? IPAddress.None : address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);

    /// <summary>Counts an auto-registration from an address when the address has not used up its registrations of the past hour.</summary>
    /// <param name="address">The client address, or null when it is not known.</param>
    /// <returns>True when the address may register the account.</returns>
    public bool TryRegister(IPAddress? address)
    {
        if (_options.RegistrationsPerAddressPerHour <= 0 || Limited(address) is not { } a)
            return true;
        var now = Environment.TickCount64;
        SweepNow(now);
        return _registrations.GetOrAdd(a, static _ => new Strikes()).TryCount(now, RegistrationWindowMs, _options.RegistrationsPerAddressPerHour);
    }

    /// <summary>Gives the address the address limits apply to: null for an unknown or loopback address.</summary>
    /// <param name="address">The client address.</param>
    /// <returns>The address, IPv4 when it is an IPv4 address mapped to IPv6, or null.</returns>
    private static IPAddress? Limited(IPAddress? address) => address is null || IPAddress.IsLoopback(address) ? null
        : address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    /// <summary>Gives the milliseconds left of the lockout of a key.</summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <param name="map">The strikes by key.</param>
    /// <param name="key">The key.</param>
    /// <param name="now">The current <see cref="Environment.TickCount64"/>.</param>
    /// <returns>The milliseconds left; 0 when the key is not locked out.</returns>
    private static long LeftMs<TKey>(ConcurrentDictionary<TKey, Strikes> map, TKey key, long now) where TKey : notnull =>
        map.TryGetValue(key, out var strikes) ? strikes.LockedMs(now) : 0;

    /// <summary>Every <see cref="SweepEvery"/> calls, removes the entries whose window and lockout are over, so that the maps stay small.</summary>
    /// <param name="now">The current <see cref="Environment.TickCount64"/>.</param>
    private void SweepNow(long now)
    {
        if (Interlocked.Increment(ref _sinceSweep) % SweepEvery != 0)
            return;
        Sweep(_byNameAndAddress, now, LockoutMs);
        Sweep(_byName, now, LockoutMs);
        Sweep(_byAddress, now, LockoutMs);
        Sweep(_registrations, now, RegistrationWindowMs);
    }

    /// <summary>Removes the entries of a map whose window and lockout are over.</summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <param name="map">The strikes by key.</param>
    /// <param name="now">The current <see cref="Environment.TickCount64"/>.</param>
    /// <param name="windowMs">The window of the map's entries in milliseconds.</param>
    private static void Sweep<TKey>(ConcurrentDictionary<TKey, Strikes> map, long now, long windowMs) where TKey : notnull
    {
        foreach (var (key, strikes) in map)
        {
            if (strikes.Expired(now, windowMs))
                map.TryRemove(new KeyValuePair<TKey, Strikes>(key, strikes));
        }
    }

    /// <summary>The counted events of one key in its current window, and its lockout.</summary>
    private sealed class Strikes
    {
        /// <summary>Held while the counts change.</summary>
        private readonly Lock _lock = new();

        /// <summary>When the current window began, in <see cref="Environment.TickCount64"/> milliseconds.</summary>
        private long _windowStartMs;

        /// <summary>The events counted in the current window.</summary>
        private int _count;

        /// <summary>Until when the key is locked out, in <see cref="Environment.TickCount64"/> milliseconds; 0 for no lockout.</summary>
        private long _lockedUntilMs;

        /// <summary>Counts an event and locks the key out once the window holds the limit of events.</summary>
        /// <param name="now">The current <see cref="Environment.TickCount64"/>.</param>
        /// <param name="windowMs">The window in milliseconds.</param>
        /// <param name="limit">The events that start a lockout.</param>
        /// <param name="lockoutMs">The lockout in milliseconds.</param>
        public void Count(long now, long windowMs, int limit, long lockoutMs)
        {
            lock (_lock)
            {
                StartWindow(now, windowMs);
                if (++_count < limit)
                    return;
                _count = 0;
                _lockedUntilMs = now + lockoutMs;
            }
        }

        /// <summary>Counts an event in the current window, without a lockout.</summary>
        /// <param name="now">The current <see cref="Environment.TickCount64"/>.</param>
        /// <param name="windowMs">The window in milliseconds.</param>
        public void Add(long now, long windowMs)
        {
            lock (_lock)
            {
                StartWindow(now, windowMs);
                _count++;
            }
        }

        /// <summary>Gives the events counted in the current window.</summary>
        /// <param name="now">The current <see cref="Environment.TickCount64"/>.</param>
        /// <param name="windowMs">The window in milliseconds.</param>
        /// <returns>The events; 0 once the window is over.</returns>
        public int InWindow(long now, long windowMs)
        {
            lock (_lock)
                return now - _windowStartMs < windowMs ? _count : 0;
        }

        /// <summary>Counts an event when the window does not yet hold the limit of events.</summary>
        /// <param name="now">The current <see cref="Environment.TickCount64"/>.</param>
        /// <param name="windowMs">The window in milliseconds.</param>
        /// <param name="limit">The events allowed per window.</param>
        /// <returns>True when the event was counted.</returns>
        public bool TryCount(long now, long windowMs, int limit)
        {
            lock (_lock)
            {
                StartWindow(now, windowMs);
                if (_count >= limit)
                    return false;
                _count++;
                return true;
            }
        }

        /// <summary>Gives the milliseconds left of the lockout.</summary>
        /// <param name="now">The current <see cref="Environment.TickCount64"/>.</param>
        /// <returns>The milliseconds left; 0 when the key is not locked out.</returns>
        public long LockedMs(long now)
        {
            lock (_lock)
                return Math.Max(0, _lockedUntilMs - now);
        }

        /// <summary>Whether the window and the lockout are over, so that the entry can be forgotten.</summary>
        /// <param name="now">The current <see cref="Environment.TickCount64"/>.</param>
        /// <param name="windowMs">The window in milliseconds.</param>
        /// <returns>True when nothing of the entry counts any more.</returns>
        public bool Expired(long now, long windowMs)
        {
            lock (_lock)
                return now - _windowStartMs >= windowMs && now >= _lockedUntilMs;
        }

        /// <summary>Starts a new window, with no events, once the current one is over.</summary>
        /// <param name="now">The current <see cref="Environment.TickCount64"/>.</param>
        /// <param name="windowMs">The window in milliseconds.</param>
        private void StartWindow(long now, long windowMs)
        {
            if (_count != 0 && now - _windowStartMs < windowMs)
                return;
            _windowStartMs = now;
            _count = 0;
        }
    }
}
