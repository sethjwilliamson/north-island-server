using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NorthIsland.GameData.Characters;
using NorthIsland.Protocol;
using NorthIsland.Protocol.IO;
using NorthIsland.Protocol.Messages.Login;
using NorthIsland.Protocol.Opcodes;

namespace NorthIsland.Server.Features.Login;

/// <summary>
/// Handles the login and character-select packets: account login, character list, slot choice, character creation and deletion, and the secret code
/// change.
/// </summary>
/// <param name="store">Account and character storage.</param>
/// <param name="world">The world state, used to find and kick an earlier session of the same account.</param>
/// <param name="sessions">The live session of each account, claimed at login.</param>
/// <param name="itemHandlers">Gives a new character its starter kit.</param>
/// <param name="formulas">Sets the initial values of a new character.</param>
/// <param name="worldEntry">Sends the world-entry packets once a character is chosen.</param>
/// <param name="bans">The account and address ban list checked at login.</param>
/// <param name="throttle">Locks out and brakes logins after too many failures and limits the auto-registrations per address.</param>
/// <param name="options">
/// Server options: server id, accepted client versions, spawn point, auto-registration and what follows character creation.
/// </param>
/// <param name="logger">Logger.</param>
public sealed partial class LoginHandlers(
    IAccountStore store,
    WorldState world,
    AccountSessions sessions,
    ItemHandlers itemHandlers,
    FormulaData formulas,
    WorldEntry worldEntry,
    BanList bans,
    LoginThrottle throttle,
    IOptions<ServerOptions> options,
    ILogger<LoginHandlers> logger)
{
    /// <summary>The first character slot of an account.</summary>
    private const byte FirstSlot = 1;

    /// <summary>The last character slot of an account (the client shows 2 slots).</summary>
    private const byte LastSlot = 2;

    /// <summary>The stat points a new character spends at creation (the client's wizard hands out exactly 5).</summary>
    private const int CreationStatPoints = 5;

    /// <summary>The highest element a new character can choose (the wizard offers elements 1..4).</summary>
    private const byte LastElement = 4;

    /// <summary>The largest colour code: nine colour digits, each 0..9.</summary>
    private const int MaxColourCode = 999_999_999;

    /// <summary>The server options, read once at construction.</summary>
    private readonly ServerOptions _options = options.Value;

    /// <summary>Held while a character name or an account slot is reserved or released.</summary>
    private readonly Lock _creating = new();

    /// <summary>The character names being created right now, ignoring case.</summary>
    private readonly HashSet<string> _namesBeingCreated = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The (login id, slot) pairs that get a character right now.</summary>
    private readonly HashSet<(int LoginId, byte Slot)> _slotsBeingFilled = [];

    /// <summary>Matches a valid account name: 4 to 14 letters or digits.</summary>
    /// <returns>The compiled expression.</returns>
    [GeneratedRegex("^[A-Za-z0-9]{4,14}$")]
    private static partial Regex AccountNamePattern();

    /// <summary>Matches a valid character name: 1 to 10 letters or digits.</summary>
    /// <returns>The compiled expression.</returns>
    [GeneratedRegex("^[A-Za-z0-9]{1,10}$")]
    private static partial Regex CharacterNamePattern();

    /// <summary>
    /// Changes the account password and answers 35/12 with the result: 1 changed, 2 wrong old password, 4 new password not 4 to 14 characters.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code: the old password, then the new one.</param>
    internal async ValueTask OnChangeAccountPassword(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.Account is not { } account || body.Length < 2)
            return;
        var r = new PacketReader(body.Span);
        var old = r.PStr8();
        var fresh = r.Remaining > 0 ? r.PStr8() : "";
        byte result = !PasswordHasher.Verify(old, account.PasswordHash) ? (byte)2 : fresh.Length is < 4 or > 14 ? (byte)4 : (byte)1;
        if (result == 1)
        {
            account.PasswordHash = PasswordHasher.Hash(fresh);
            await store.SaveAsync(account).ConfigureAwait(false);
        }
        session.Send(new PacketWriter(Action35.Code, Action35.ChangeAccountPassword).U8(result).ToArray());
        if (MayLog(session))
            logger.LogInformation("[{Session}] {Name}: change account password -> {Result}", session.Id, account.Name, result);
    }

    /// <summary>
    /// Changes the active character's secret code and answers with the result: 1 changed, 3 wrong old code, 4 new code shorter than 6 characters.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code.</param>
    internal async ValueTask OnChangePassword(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.Account is not { } account || session.ActiveCharacter is not { } character || body.Length < 2)
            return;
        var r = new PacketReader(body.Span);
        var old = r.PStr8();
        var fresh = r.Remaining > 0 ? r.PStr8() : "";
        byte result = !string.IsNullOrEmpty(character.Cipher) && character.Cipher != old ? (byte)3 : fresh.Length < 6 ? (byte)4 : (byte)1;
        if (result == 1)
        {
            character.Cipher = fresh;
            await store.SaveAsync(account).ConfigureAwait(false);
        }
        session.Send(new PacketWriter(Action35.Code, Action35.ChangePassword).U8(result).ToArray());
        if (MayLog(session))
            logger.LogInformation("[{Session}] {Name}: change secret code -> {Result}", session.Id, character.Name, result);
    }

    /// <summary>
    /// Answers the client's first packet with the server identity and moves the session to the login stage; ignored when the session is not newly
    /// connected.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code.</param>
    internal ValueTask OnHello(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.State != SessionState.Connected)
        {
            if (MayLog(session))
                logger.LogInformation("[{Session}] action 0 in state {State} (kick acknowledge)", session.Id, session.State);
            return ValueTask.CompletedTask;
        }

        session.Send(ServerMessages.ServerSelected(_options.ServerId, _options.ServerFlag, _options.ServerName));
        session.State = SessionState.AwaitLogin;
        return ValueTask.CompletedTask;
    }

    /// <summary>Handles a login by account name and password.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code.</param>
    internal async ValueTask OnAccountLogin(GameSession session, ReadOnlyMemory<byte> body)
    {
        var req = AccountLoginRequest.Parse(body.Span);
        await LoginAsync(session, req.Account, req.Password, req.Version, req.ItemDatSize).ConfigureAwait(false);
    }

    /// <summary>Handles a login by a prefix and a number; the account name is the trimmed prefix followed by the number.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code.</param>
    internal async ValueTask OnNumericLogin(GameSession session, ReadOnlyMemory<byte> body)
    {
        var req = NumericLoginRequest.Parse(body.Span);
        var account = req.Prefix.Trim() + req.LoginNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await LoginAsync(session, account, req.Password, req.Version, req.ItemDatSize).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs a login: checks the client version, the Item.Dat size, the name format, the bans and the lockouts, waits out the name's brake when it
    /// failed too often from all addresses (<see cref="LoginThrottle.BrakeMs"/>), verifies the password (creating
    /// the account when auto-registration is on, the name is not a GmAccounts name and the address has registrations left), claims the account for
    /// the session and kicks its earlier session (in the world or at character select), clears the admin mode, makes a GmAccounts name a GM, then
    /// sends the login id and the character list. A wrong password or an unknown name counts towards the lockouts, and a connection whose logins
    /// fail too often is closed.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="name">The account name.</param>
    /// <param name="password">The password as typed.</param>
    /// <param name="version">The client version number.</param>
    /// <param name="itemDatSize">Size of the client's Item.Dat, or null when the client did not send one.</param>
    private async Task LoginAsync(
        GameSession session,
        string name,
        string password,
        ushort version,
        long? itemDatSize)
    {
        if (session.State != SessionState.AwaitLogin)
        {
            if (MayLog(session))
                logger.LogWarning("[{Session}] login in state {State}, ignored", session.Id, session.State);
            return;
        }

        if (MayLog(session))
        {
            logger.LogInformation("[{Session}] login '{Name}' version {Version} Item.Dat size {ItemDat}",
                session.Id, name, version, itemDatSize?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "(none)");
        }

        if (!_options.AcceptsClientVersion(version))
        {
            session.Send(ServerMessages.Kick(KickReason.FalseEdition));
            return;
        }
        session.Client = ClientVersions.For(version);
        if (_options.EnforceItemDatSize && _options.ItemDatSize is { } expected && itemDatSize != expected)
        {
            session.Send(ServerMessages.Kick(KickReason.InvalidItemVersion));
            return;
        }
        if (!AccountNamePattern().IsMatch(name) || password.Length == 0)
        {
            Refuse(session, ServerMessages.LoginRejected(Action1.WrongAccountOrPassword));
            return;
        }
        var address = session.RemoteEndPoint?.Address;
        if (bans.IsAccountBanned(name) || bans.IsAddressBanned(address))
        {
            if (MayLog(session))
                logger.LogWarning("[{Session}] login '{Name}' from {Address} refused: banned", session.Id, name, address);
            session.Send(ServerMessages.Locked(account: true, GmCommands.BanSeconds));
            return;
        }
        if (throttle.LockedSeconds(name, address) is var locked and > 0)
        {
            if (MayLog(session))
            {
                logger.LogWarning("[{Session}] login '{Name}' from {Address} refused: locked out for {Seconds} s after failed logins",
                    session.Id, name, address, locked);
            }
            Refuse(session, ServerMessages.Locked(account: true, locked));
            return;
        }
        if (throttle.BrakeMs(name) is var brake and > 0)
        {
            await Task.Delay(brake).ConfigureAwait(false);
            if (session.Closing)
                return;
        }

        var account = await store.FindByNameAsync(name).ConfigureAwait(false);
        var created = false;
        if (account is null)
        {
            if (!_options.AutoRegister || _options.ListsGm(name))
            {
                if (_options.AutoRegister && MayLog(session))
                    logger.LogWarning("[{Session}] login '{Name}' refused: a GmAccounts name is never registered by a login", session.Id, name);
                Failed(session, name, address);
                return;
            }
            if (!throttle.TryRegister(address))
            {
                if (MayLog(session))
                {
                    logger.LogWarning("[{Session}] login '{Name}' from {Address} refused: the address registered its accounts for this hour",
                        session.Id, name, address);
                }
                Refuse(session, ServerMessages.LoginRejected(Action1.WrongAccountOrPassword));
                return;
            }
            try
            {
                account = await store.CreateAsync(name, password).ConfigureAwait(false);
                created = true;
            }
            catch (InvalidOperationException) // another login registered the name first: check the password against that account
            {
                account = await store.FindByNameAsync(name).ConfigureAwait(false);
            }
        }
        if (account is null || (!created && !PasswordHasher.Verify(password, account.PasswordHash)))
        {
            Failed(session, name, address);
            return;
        }

        throttle.Succeeded(name, address);
        session.Login.FailedLogins = 0;
        session.Account = account;
        session.AdminMode = false;
        KickEarlierSessions(session, account);

        account.LastLoginUtc = GameClock.UtcNow;
        if (!account.Gm && _options.ListsGm(account.Name))
        {
            account.Gm = true;
            logger.LogInformation("[{Session}] {Name} is made a GM by the GmAccounts setting", session.Id, account.Name);
        }
        await store.SaveAsync(account).ConfigureAwait(false);

        session.Send(ServerMessages.LoginId(account.LoginId));
        SendCharacterList(session);
    }

    /// <summary>Counts a wrong password or an unknown account name towards the lockouts and refuses the login.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="name">The account name.</param>
    /// <param name="address">The client address, or null when it is not known.</param>
    private void Failed(GameSession session, string name, System.Net.IPAddress? address)
    {
        throttle.Failed(name, address);
        if (MayLog(session))
            logger.LogWarning("[{Session}] login '{Name}' from {Address} refused: wrong account or password", session.Id, name, address);
        Refuse(session, ServerMessages.LoginRejected(Action1.WrongAccountOrPassword));
    }

    /// <summary>
    /// Sends the refusal of a login and counts it for the connection; once the connection reaches the failures per connection, it is told that
    /// the login failed too often and closed.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="refusal">The refusal packet.</param>
    private void Refuse(GameSession session, byte[] refusal)
    {
        session.Send(refusal);
        if (++session.Login.FailedLogins < Math.Max(1, _options.LoginFailuresPerConnection))
            return;
        if (MayLog(session))
            logger.LogWarning("[{Session}] {Count} failed logins on this connection; disconnected", session.Id, session.Login.FailedLogins);
        session.Send(ServerMessages.Kick(KickReason.LoginFailedThreeTimes));
        session.Close();
    }

    /// <summary>
    /// Makes a session the live session of its account and kicks the account's earlier session, whether it is in the world or still at character
    /// select, and any other session of the account found in the world.
    /// </summary>
    /// <param name="session">The session that just logged in; its account is set.</param>
    /// <param name="account">The account it logged in to.</param>
    private void KickEarlierSessions(GameSession session, Account account)
    {
        var earlier = sessions.Claim(account, session);
        foreach (var previous in new[] { earlier, world.SessionOf(account.LoginId) }.OfType<GameSession>().Distinct())
        {
            if (ReferenceEquals(previous, session))
                continue;
            if (MayLog(session))
                logger.LogInformation("[{Session}] {Name} logged in again, kicking session {Previous}", session.Id, account.Name, previous.Id);
            previous.Send(ServerMessages.Kick(KickReason.RepeatedLogin));
            world.Disconnect(session, previous);
        }
    }

    /// <summary>
    /// Whether a session may still act on its account at character select: it is logged in and no later login of the account replaced it. Logs
    /// the refusal otherwise.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="what">The packet, for the log.</param>
    /// <returns>True when the session holds its account.</returns>
    private bool HoldsAccount(GameSession session, string what)
    {
        if (sessions.Holds(session))
            return true;
        if (MayLog(session))
            logger.LogWarning("[{Session}] {What} from a session that no longer holds its account, ignored", session.Id, what);
        return false;
    }

    /// <summary>
    /// Takes a line from the session's budget of problem log lines (<see cref="GameSession.ProblemLog"/>), so that a client repeating a refused or
    /// ignored packet cannot flood the log; logs first how many lines the budget held back.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <returns>True when the line may be logged.</returns>
    private bool MayLog(GameSession session) => session.ProblemLog.TryTake(logger, session.Id);

    /// <summary>Sends the account's characters ordered by slot and moves the session to character select.</summary>
    /// <param name="session">The player's session.</param>
    private static void SendCharacterList(GameSession session)
    {
        var account = session.Account!;
        session.Send(ServerMessages.CharacterList(account.Characters.OrderBy(c => c.Slot).Select(ToSummary)));
        session.State = SessionState.CharacterSelect;
    }

    /// <summary>
    /// Handles the choice of a slot: enters the world with the character in it, or starts character creation when the slot is empty.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code.</param>
    internal async ValueTask OnSelectSlot(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.State != SessionState.CharacterSelect || session.Account is null)
        {
            if (MayLog(session))
                logger.LogWarning("[{Session}] 63/2 in state {State}, ignored", session.Id, session.State);
            return;
        }
        if (!HoldsAccount(session, "63/2"))
            return;

        var slot = SelectSlotRequest.Parse(body.Span).Slot;
        if (slot is < FirstSlot or > LastSlot)
        {
            if (MayLog(session))
                logger.LogWarning("[{Session}] 63/2 for slot {Slot}, refused", session.Id, slot);
            SendCharacterList(session);
            return;
        }
        session.Login.SelectedSlot = slot;
        var character = session.Account.CharacterInSlot(slot);
        if (character is null)
        {
            session.Login.PendingName = null;
            session.Send(ServerMessages.StartCharacterCreation(_options.SkipCreationCipher));
            session.State = SessionState.Creating;
            return;
        }

        await worldEntry.EnterWorldAsync(session, character).ConfigureAwait(false);
    }

    /// <summary>
    /// Releases the session's claim on its account, forgets the account and the admin mode, and returns the session to the stage that waits for a
    /// login, with a new login timeout; ignored once the session is in the world, which it leaves only by disconnecting.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code.</param>
    internal ValueTask OnBackToLogin(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.State is not (SessionState.AwaitLogin or SessionState.CharacterSelect or SessionState.Creating))
        {
            if (MayLog(session))
                logger.LogWarning("[{Session}] 63/0 in state {State}, ignored", session.Id, session.State);
            return ValueTask.CompletedTask;
        }
        if (session.State != SessionState.AwaitLogin)
            session.RestartLoginTimer();
        sessions.Release(session);
        session.Account = null;
        session.AdminMode = false;
        session.Login.PendingName = null;
        session.State = SessionState.AwaitLogin;
        return ValueTask.CompletedTask;
    }

    /// <summary>Drops the pending character name and returns the session to character select; ignored outside character creation.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code.</param>
    internal ValueTask OnCancelCreation(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.State != SessionState.Creating)
        {
            if (MayLog(session))
                logger.LogWarning("[{Session}] 63/3 in state {State}, ignored", session.Id, session.State);
            return ValueTask.CompletedTask;
        }
        session.Login.PendingName = null;
        session.State = SessionState.CharacterSelect;
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Checks a proposed character name (valid format and not taken) and answers with the result; remembers the name as pending when it is accepted.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code.</param>
    internal async ValueTask OnCheckName(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.State != SessionState.Creating)
        {
            if (MayLog(session))
                logger.LogWarning("[{Session}] 9/2 in state {State}, ignored", session.Id, session.State);
            return;
        }

        var name = CheckNameRequest.Parse(body.Span).Name;
        NameCheck result;
        if (!CharacterNamePattern().IsMatch(name))
            result = NameCheck.Invalid;
        else if (await store.IsCharacterNameTakenAsync(name).ConfigureAwait(false))
            result = NameCheck.Unavailable;
        else
            result = NameCheck.Ok;

        session.Login.PendingName = result == NameCheck.Ok ? name : null;
        session.Send(ServerMessages.NameCheckResult(result));
    }

    /// <summary>
    /// Creates the character in the selected slot from the pending name and the creation choices, then enters the world or resends the character
    /// list, as configured. Resends the character list when a choice is outside what the creation wizard offers, and answers that the name is
    /// unavailable when the slot or the name was taken meanwhile (the name and the slot are reserved while the check and the add run, so two
    /// sessions cannot both take them).
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code.</param>
    internal async ValueTask OnCreateCharacter(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.State != SessionState.Creating || session.Account is null || session.Login.PendingName is not { } name)
        {
            if (MayLog(session))
                logger.LogWarning("[{Session}] 9/1 in state {State} without an accepted name, ignored", session.Id, session.State);
            return;
        }
        if (!HoldsAccount(session, "9/1"))
            return;

        var req = CreateCharacterRequest.Parse(body.Span);
        var account = session.Account;
        var slot = session.Login.SelectedSlot;
        if (InvalidCreation(req, slot) is { } reason)
        {
            if (MayLog(session))
                logger.LogWarning("[{Session}] 9/1 refused: {Reason}", session.Id, reason);
            session.Login.PendingName = null;
            SendCharacterList(session);
            return;
        }

        if (!TryReserve(name, account.LoginId, slot))
        {
            session.Send(ServerMessages.NameCheckResult(NameCheck.Unavailable));
            return;
        }
        Character character;
        try
        {
            if (account.CharacterInSlot(slot) is not null || await store.IsCharacterNameTakenAsync(name).ConfigureAwait(false))
            {
                session.Send(ServerMessages.NameCheckResult(NameCheck.Unavailable));
                return;
            }
            character = NewCharacter(req, name, slot);
            account.Characters.Add(character);
            await store.SaveAsync(account).ConfigureAwait(false);
        }
        finally
        {
            Unreserve(name, account.LoginId, slot);
        }
        session.Login.PendingName = null;
        if (MayLog(session)) // a client may create and delete characters at will
        {
            logger.LogInformation("[{Session}] created character '{Name}' in slot {Slot} for {Account}",
                session.Id, character.Name, character.Slot, account.Name);
        }

        if (_options.AfterCreate == AfterCreate.EnterWorld)
            await worldEntry.EnterWorldAsync(session, character).ConfigureAwait(false);
        else
            SendCharacterList(session);
    }

    /// <summary>
    /// Checks the creation choices against what the creation wizard offers: a slot 1..2, a creatable body type and head, sprite 0, colour codes of
    /// nine digits, an element 1..4 and stats that spend exactly the 5 creation points.
    /// </summary>
    /// <param name="req">The creation request.</param>
    /// <param name="slot">The slot chosen on the character select screen.</param>
    /// <returns>Why the choices are refused, or null when they are valid.</returns>
    private static string? InvalidCreation(CreateCharacterRequest req, byte slot)
    {
        if (slot is < FirstSlot or > LastSlot)
            return $"slot {slot}";
        if (RoleData.RoleIndex(req.BodyType, req.Head) == 0) // 0: not a creatable role
            return $"body type {req.BodyType} with head {req.Head}";
        if (req.Sprite != 0) // the wizard always sends body sprite 0
            return $"sprite {req.Sprite}";
        if (req.Colour1 is < 0 or > MaxColourCode || req.Colour2 is < 0 or > MaxColourCode)
            return $"colour codes {req.Colour1}, {req.Colour2}";
        if (req.Element is < 1 or > LastElement)
            return $"element {req.Element}";
        if (req.Stats.Sum(s => s) != CreationStatPoints) // bytes are never negative, so each stat is then 0..5 too
            return $"stats {string.Join(',', req.Stats)}";
        return null;
    }

    /// <summary>Reserves a character name and an account slot for a creation.</summary>
    /// <param name="name">The character name.</param>
    /// <param name="loginId">The account's login id.</param>
    /// <param name="slot">The slot.</param>
    /// <returns>True when neither was reserved by another creation.</returns>
    private bool TryReserve(string name, int loginId, byte slot)
    {
        lock (_creating)
        {
            if (_namesBeingCreated.Contains(name) || _slotsBeingFilled.Contains((loginId, slot)))
                return false;
            _namesBeingCreated.Add(name);
            _slotsBeingFilled.Add((loginId, slot));
            return true;
        }
    }

    /// <summary>Releases the reservation of a creation.</summary>
    /// <param name="name">The character name.</param>
    /// <param name="loginId">The account's login id.</param>
    /// <param name="slot">The slot.</param>
    private void Unreserve(string name, int loginId, byte slot)
    {
        lock (_creating)
        {
            _namesBeingCreated.Remove(name);
            _slotsBeingFilled.Remove((loginId, slot));
        }
    }

    /// <summary>Builds a new character from the creation choices, with its starting attributes, initial values and starter kit.</summary>
    /// <param name="req">The creation request.</param>
    /// <param name="name">The accepted name.</param>
    /// <param name="slot">The slot it goes into.</param>
    /// <returns>The character.</returns>
    private Character NewCharacter(CreateCharacterRequest req, string name, byte slot)
    {
        var character = new Character
        {
            Slot = slot,
            Name = name,
            CreatedUtc = GameClock.UtcNow,
            BodyType = req.BodyType,
            Head = req.Head,
            Sprite = req.Sprite,
            Colour1 = req.Colour1,
            Colour2 = req.Colour2,
            Element = req.Element,
            CreationStats = [.. req.Stats],
            Cipher = req.Cipher,
            MapId = _options.Spawn.MapId,
            X = _options.Spawn.X,
            Y = _options.Spawn.Y,
        };
        worldEntry.ApplyStartingAttributes(character);
        formulas.InitialiseNew(character);
        itemHandlers.EnsureStarterKit(character);
        return character;
    }

    /// <summary>
    /// Deletes the character in the requested slot and answers with a result: 1 deleted, 3 wrong secret code, 4 not possible now (not at
    /// character select, the account was taken over by a later login, or the character is in the world) or no such character.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code.</param>
    internal async ValueTask OnDeleteCharacter(GameSession session, ReadOnlyMemory<byte> body)
    {
        var req = DeleteCharacterRequest.Parse(body.Span);
        var account = session.Account;
        var character = account?.CharacterInSlot(req.Slot);
        byte result;
        if (session.State != SessionState.CharacterSelect || account is null || character is null || !HoldsAccount(session, "35/2"))
            result = 4;
        else if (world.SessionOf(account.LoginId)?.ActiveCharacter == character)
        {
            if (MayLog(session))
                logger.LogWarning("[{Session}] 35/2 for {Name}, who is in the world, refused", session.Id, character.Name);
            result = 4;
        }
        else if (!string.IsNullOrEmpty(character.Cipher) && character.Cipher != req.Cipher)
            result = 3;
        else
        {
            account.Characters.Remove(character);
            await store.SaveAsync(account).ConfigureAwait(false);
            result = 1;
        }
        session.Send(ServerMessages.DeleteCharacterResult(result, req.Slot));
    }

    /// <summary>Builds the character list entry of a character.</summary>
    /// <param name="c">The character.</param>
    /// <returns>The list entry.</returns>
    private static CharacterSummary ToSummary(Character c) => new()
    {
        Slot = c.Slot,
        Name = c.Name,
        Level = c.Level,
        Element = c.Element,
        MaxHp = c.MaxHp,
        Hp = c.Hp,
        MaxSp = c.MaxSp,
        Sp = c.Sp,
        Exp = c.Exp,
        Gold = c.Gold,
        BodyType = c.BodyType,
        Head = c.Head,
        Sprite = c.Sprite,
        Colour1 = c.Colour1,
        Colour2 = c.Colour2,
        RebornCount = c.RebornCount,
        Job = c.Job,
        EquippedItems = [.. c.EquippedItemIds()],
    };
}
