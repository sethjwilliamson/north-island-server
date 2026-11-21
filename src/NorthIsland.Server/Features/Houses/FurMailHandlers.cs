using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NorthIsland.Protocol.IO;
using NorthIsland.Protocol.Messages.Player;
using NorthIsland.Protocol.Opcodes;
using NorthIsland.Protocol.Text;

namespace NorthIsland.Server.Features.Houses;

/// <summary>The Mailbox piece: a player who has one placed can send letters to any character, who finds them when they log in.</summary>
/// <param name="store">The account store, to find characters that are offline.</param>
/// <param name="world">The world state, to find online players.</param>
/// <param name="sessions">The live session of each account, to find a target still at character select.</param>
/// <param name="options">The house options, for the mailbox size.</param>
/// <param name="logger">The logger.</param>
public sealed class FurMailHandlers(
    IAccountStore store,
    WorldState world,
    AccountSessions sessions,
    IOptions<HouseOptions> options,
    ILogger<FurMailHandlers> logger) : IDisposable
{
    /// <summary>The item id of the Mailbox.</summary>
    public const ushort Mailbox = 39084; // Mailbox

    /// <summary>Longest letter text in characters, as the client's letter form allows (views/mail/FurMailWindow.xml).</summary>
    public const int MaxText = 152;

    /// <summary>The most letters a box keeps for a character when <see cref="HouseLifeOptions.FurMailCapacity"/> is 0 or less.</summary>
    public const int DefaultCapacity = 100;

    /// <summary>
    /// Held while a letter goes into the box of a character whose account has no live session, and the account is saved. Their senders hold no
    /// gate of theirs, so this keeps two of them from filling the box at once, or one from adding a letter while the save of another writes the
    /// box. Once the account has a live session its gate holds the box instead (<see cref="PostLiveAsync"/>).
    /// </summary>
    private readonly SemaphoreSlim _offlineBoxes = new(1, 1);

    /// <summary>Lets go of <see cref="_offlineBoxes"/>; called by the service container when the server stops.</summary>
    public void Dispose() => _offlineBoxes.Dispose();

    /// <summary>Whether a character has a Mailbox placed.</summary>
    /// <param name="c">The character.</param>
    /// <returns>True when a Mailbox piece is among their furniture.</returns>
    public static bool HasMailbox(Character c) => c.Furniture.Exists(f => f.ItemId == Mailbox);

    /// <summary>Tells the client whether the player has a Mailbox.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="c">The player's character.</param>
    public static void SendState(GameSession session, Character c) =>
        session.Send(FurMailMessages.Result(HasMailbox(c) ? (byte)3 : (byte)4)); // result

    /// <summary>At login sends the Mailbox state and the letters that arrived while the player was offline, then empties the box.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="c">The player's character.</param>
    public static void SendOnLogin(GameSession session, Character c)
    {
        if (HasMailbox(c))
            SendState(session, c);
        FurMail[] letters;
        lock (c.FurMails) // a sender who saw the character offline may still be adding a letter
        {
            letters = [.. c.FurMails];
            c.FurMails.Clear();
        }
        foreach (var letter in letters)
            session.Send(FurMailMessages.Letter(letter.SenderId, letter.SenderName, letter.Time, letter.Text));
    }

    /// <summary>
    /// Handles <c>14/6</c>: sends a letter to the named character, either to their session when it is the character being played or into their
    /// box. The text is capped at the client's length, the session must have waited the friend mail's wait, and a full box refuses it. Whether
    /// the target's account is online is checked again once its box is held (<see cref="PostOfflineAsync"/>, <see cref="PostLiveAsync"/>).
    /// </summary>
    /// <param name="session">The sender's session.</param>
    /// <param name="body">The sender id (four bytes), the length of the name, the name, then the letter text.</param>
    internal async ValueTask OnLetter(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.State != SessionState.InWorld || session.ActiveCharacter is not { } c || session.Account is null || body.Length < 5)
            return;
        var nameLength = body.Span[4];
        if (body.Length < 5 + nameLength)
            return;
        var name = GameEncoding.GetString(body.Span.Slice(5, nameLength)).Trim();
        var text = GameEncoding.GetString(body.Span[(5 + nameLength)..]);
        var myId = session.Account.LoginId;
        if (!HasMailbox(c))
        {
            session.Send(FurMailMessages.Result(2)); // result
            return;
        }
        var target = store.All().SelectMany(a => a.Characters.Select(ch => (Account: a, Character: ch)))
            .FirstOrDefault(x => string.Equals(x.Character.Name, GmTag.Strip(name), StringComparison.OrdinalIgnoreCase) && x.Account.LoginId != myId);
        if (target.Character is null || text.Length == 0)
        {
            session.Send(FurMailMessages.Result(1)); // result 1 (FurMailWindow.cpp): "player doesn't exist", the client puts the text back
            return;
        }
        // the wait is only checked here; the turn is taken once the letter is accepted, so a busy target can be tried again at once
        if (text.Length > MaxText || GameClock.UtcNow - session.Friends.LastMailUtc < FriendHandlers.MailWait)
        {
            logger.LogWarning("[{Session}] {Name}: letter to {Target} refused ({Length} characters, or too soon)", session.Id, c.Name, name,
                text.Length);
            session.Send(FurMailMessages.Result(1)); // result 1 (FurMailWindow.cpp): "player doesn't exist", the client puts the text back
            return;
        }
        var targetId = target.Account.LoginId;
        var letter = new FurMail { SenderId = myId, SenderName = c.Name, Time = GameClock.Now.ToOADate(), Text = text };
        var live = LiveSession(targetId);
        if (live is null)
        {
            var posted = await PostOfflineAsync(session, target.Account, target.Character, letter).ConfigureAwait(false);
            if (posted == Post.Refused)
                return;
            if (posted == Post.CameOnline && (live = LiveSession(targetId)) is null) // logged in and out again meanwhile
            {
                logger.LogWarning("[{Session}] {Name}: letter to {Target} refused, the target's account was logging in or out", session.Id, c.Name,
                    target.Character.Name);
                session.Send(FurMailMessages.Result(1)); // result 1 (FurMailWindow.cpp): "player doesn't exist", the client puts the text back
                return;
            }
        }
        if (live is not null && !await PostLiveAsync(session, c, live, target.Account, target.Character, letter).ConfigureAwait(false))
            return;
        _ = FriendHandlers.TakeMailTurn(session); // the wait was checked above and the sender's gate is held, so this starts the next wait
        session.Send(FurMailMessages.Result(3)); // result
        logger.LogInformation("[{Session}] {Name} leaves a letter in {Target}'s mailbox: {Text}", session.Id, c.Name, target.Character.Name, text);
    }

    /// <summary>What became of a letter offered to a box.</summary>
    private enum Post
    {
        /// <summary>The letter is in the box.</summary>
        InBox,

        /// <summary>The target blocks the sender, the box is full or the save failed; the sender was told.</summary>
        Refused,

        /// <summary>The target's account has a live session now, whose gate holds the box; nothing was posted.</summary>
        CameOnline,
    }

    /// <summary>
    /// Gives the session that holds an account: the one in the world, or else the one still at character select (<see
    /// cref="AccountSessions"/>), which also holds the account's characters until it leaves.
    /// </summary>
    /// <param name="loginId">The account's login id.</param>
    /// <returns>The live session, or null when the account is offline.</returns>
    private GameSession? LiveSession(int loginId) => world.SessionOf(loginId) ?? sessions.SessionOf(loginId);

    /// <summary>
    /// Delivers a letter to an account with a live session, under that session's gate (the rule of <see cref="GameSession.TryExclusiveFrom"/>):
    /// sent at once when the session plays the target character, otherwise put into the target's box and the account saved, since a session at
    /// character select never saves it. A busy session, or one whose connection ended, refuses the letter; the sender tries again.
    /// </summary>
    /// <param name="session">The sender's session.</param>
    /// <param name="c">The sender's character.</param>
    /// <param name="live">The target account's live session.</param>
    /// <param name="account">The target's account.</param>
    /// <param name="target">The character the letter is for.</param>
    /// <param name="letter">The letter.</param>
    /// <returns>True when the letter was sent or is in the box; false when it was refused and the sender was told.</returns>
    private async ValueTask<bool> PostLiveAsync(GameSession session, Character c, GameSession live, Account account, Character target,
        FurMail letter)
    {
        if (!live.TryExclusiveFrom(session, out var held))
        {
            logger.LogWarning("[{Session}] {Name}: letter to {Target} refused, the target's session is busy or its connection ended", session.Id,
                c.Name, target.Name);
            session.Send(FurMailMessages.Result(1)); // result 1 (FurMailWindow.cpp): "player doesn't exist", the client puts the text back
            return false;
        }
        using (held)
        {
            if (ReferenceEquals(live.ActiveCharacter, target)) // checked under the gate: the world entry, which empties the box, is over
            {
                if (BlocklistHandlers.Blocks(target, letter.SenderId))
                {
                    BlocklistHandlers.Refuse(session, 2); // action
                    return false;
                }
                live.Send(FurMailMessages.Letter(letter.SenderId, letter.SenderName, letter.Time, letter.Text));
                return true;
            }
            return TryPost(session, target, letter, offlineId: null) == Post.InBox
                && await SaveBoxAsync(session, account, target, letter).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Puts a letter into the box of a character whose account has no live session and saves the account, under <see cref="_offlineBoxes"/>;
    /// gives up without posting when the account got a live session meanwhile, whose gate then holds the box.
    /// </summary>
    /// <param name="session">The sender's session.</param>
    /// <param name="account">The target's account.</param>
    /// <param name="target">The character the letter is for.</param>
    /// <param name="letter">The letter.</param>
    /// <returns>What became of the letter.</returns>
    private async ValueTask<Post> PostOfflineAsync(GameSession session, Account account, Character target, FurMail letter)
    {
        await _offlineBoxes.WaitAsync().ConfigureAwait(false); // no session gate: the semaphore is never held while waiting for one
        try
        {
            var posted = TryPost(session, target, letter, account.LoginId);
            if (posted == Post.InBox && !await SaveBoxAsync(session, account, target, letter).ConfigureAwait(false))
                return Post.Refused; // taken back out of the box; the sender was told
            return posted;
        }
        finally
        {
            _offlineBoxes.Release();
        }
    }

    /// <summary>
    /// Saves an account after a letter went into one of its boxes. When the save fails (a login of the account may change it while it is
    /// written) the letter is taken back out and the sender told, so that a retry does not post it twice; a letter that a login already took
    /// out of the box (SendOnLogin) was delivered and counts as sent.
    /// </summary>
    /// <param name="session">The sender's session.</param>
    /// <param name="account">The target's account.</param>
    /// <param name="target">The character whose box holds the letter.</param>
    /// <param name="letter">The letter just posted.</param>
    /// <returns>True when the letter counts as sent; false when it was taken back and the sender was told.</returns>
    private async ValueTask<bool> SaveBoxAsync(GameSession session, Account account, Character target, FurMail letter)
    {
        try
        {
            await store.SaveAsync(account).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            int taken;
            lock (target.FurMails) // the character's login may be taking the letters out (SendOnLogin)
                taken = target.FurMails.RemoveAll(m => ReferenceEquals(m, letter));
            if (taken == 0)
            {
                logger.LogWarning(ex, "[{Session}] saving {Account} after a letter failed; a login already took the letter", session.Id,
                    account.Name);
                return true;
            }
            logger.LogWarning(ex, "[{Session}] saving {Account} after a letter failed; the letter is taken back", session.Id, account.Name);
            session.Send(FurMailMessages.Result(1)); // result 1 (FurMailWindow.cpp): "player doesn't exist", the client puts the text back
            return false;
        }
    }

    /// <summary>
    /// Puts a letter into a character's box unless they block the sender or the box is full, telling the sender why it is refused; the caller
    /// holds the box (the target's session gate, or <see cref="_offlineBoxes"/>).
    /// </summary>
    /// <param name="session">The sender's session.</param>
    /// <param name="target">The character the letter is for.</param>
    /// <param name="letter">The letter.</param>
    /// <param name="offlineId">
    /// The target's login id when the caller holds <see cref="_offlineBoxes"/>, or null under the target's session gate. The account is checked
    /// for a live session under the box's lock: a login claims the account before its world entry empties the box under the same lock, so a
    /// letter posted here is either taken at that login or posted before its claim.
    /// </param>
    /// <returns>What became of the letter.</returns>
    private Post TryPost(GameSession session, Character target, FurMail letter, int? offlineId)
    {
        var capacity = options.Value.HouseLife.FurMailCapacity is > 0 and var set ? set : DefaultCapacity;
        bool blocked;
        lock (target.FurMails) // the character's login may be taking the letters out (SendOnLogin)
        {
            if (offlineId is { } id && LiveSession(id) is not null)
                return Post.CameOnline;
            blocked = BlocklistHandlers.Blocks(target, letter.SenderId);
            if (!blocked && target.FurMails.Count < capacity)
            {
                target.FurMails.Add(letter);
                return Post.InBox;
            }
        }
        if (blocked)
            BlocklistHandlers.Refuse(session, 2); // action
        else
            session.Send(new PacketWriter(Action14.Code, Action14.MailboxFull).ToArray());
        return Post.Refused;
    }
}
