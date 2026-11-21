using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using NorthIsland.Protocol.Messages.Player;
using NorthIsland.Protocol.Text;

namespace NorthIsland.Server.Features.Social;

/// <summary>Handles the friend list: requests, answers, deletion and mail.</summary>
/// <param name="store">The account store, to save both sides and find offline players.</param>
/// <param name="world">World state.</param>
/// <param name="logger">Logger.</param>
public sealed class FriendHandlers(
    IAccountStore store,
    WorldState world,
    ILogger<FriendHandlers> logger)
{
    /// <summary>Longest mail text in characters, as the client's mail form allows (views/mail/SendMailWindow.xml).</summary>
    public const int MaxMailText = 152;

    /// <summary>Most mails that wait in the mailbox of an offline character; further mails are dropped until they log in.</summary>
    public const int MaxMailbox = 100;

    /// <summary>Shortest time between two mails of one session.</summary>
    public static readonly TimeSpan MailWait = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Sends the friend list on login: the GM center entry, every friend with fresh data and online state, tells friends the player is online, and
    /// delivers waiting mail.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="character">The player's character.</param>
    public void SendOnLogin(GameSession session, Character character)
    {
        session.Send(FriendMessages.Entry(new FriendInfo(100, "GM center", 0, 0, 0, 0, 0, 0, ""))); // Id
        foreach (var friend in character.Friends)
        {
            if (world.SessionOf(friend.Id) is { } online && online.ActiveCharacter is { } fc)
                Refresh(friend, fc);
            session.Send(FriendMessages.Entry(Info(friend)));
        }
        if (character.Friends.Count > 0)
            session.Send(FriendMessages.OnlineStates(character.Friends.Select(f => (f.Id, world.IsOnline(f.Id)))));
        world.TellFriends(session, online: true);
        foreach (var mail in character.Mailbox)
            session.Send(FriendMessages.Mail(mail.SenderId, mail.Time, mail.Text));
        if (character.Mailbox.Count > 0)
            logger.LogInformation("[{Session}] {Name}: {Count} mail(s) delivered", session.Id, character.Name, character.Mailbox.Count);
        character.Mailbox.Clear();
    }

    /// <summary>Handles a friend request to another player who is online and not yet a friend.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: the target's login id.</param>
    internal ValueTask OnRequest(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (InWorld(session) is not { } character || body.Length < 4)
            return ValueTask.CompletedTask;
        var targetId = BinaryPrimitives.ReadInt32LittleEndian(body.Span);
        var myId = session.Account!.LoginId;
        var target = targetId != myId ? world.SessionOf(targetId) : null;
        var other = target?.ActiveCharacter;
        string? refusal = targetId == myId ? "oneself"
            : other is null ? "not online"
                : character.Friends.Exists(f => f.Id == targetId) ? "already a friend"
                    : character.Friends.Count >= FriendEntry.MaxFriends ? "own list full"
                        : null;
        if (refusal is not null)
        {
            logger.LogInformation("[{Session}] {Name}: friend request to {Target} refused ({Why})", session.Id, character.Name, targetId, refusal);
            if (refusal == "not online")
                session.Send(FriendMessages.Answer(targetId, 3));
            return ValueTask.CompletedTask;
        }
        if (BlocklistHandlers.Blocks(other, myId))
        {
            BlocklistHandlers.Refuse(session, 3);
            return ValueTask.CompletedTask;
        }
        if (other!.Friends.Count >= FriendEntry.MaxFriends)
        {
            session.Send(FriendMessages.ListFull());
            return ValueTask.CompletedTask;
        }
        target!.Friends.PendingFrom = myId;
        target.Send(FriendMessages.Request(myId));
        logger.LogInformation("[{Session}] {Name} asks {Other} to be friends", session.Id, character.Name, other.Name);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Handles the answer to a friend request; on acceptance both players get each other as friends and are saved, under the requester's session
    /// gate; a busy requester session refuses the acceptance as a full list would.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: the requester's login id and the answer (1 yes, 2 no).</param>
    internal async ValueTask OnAnswer(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (InWorld(session) is not { } character || body.Length < 5)
            return;
        var requesterId = BinaryPrimitives.ReadInt32LittleEndian(body.Span);
        var answer = body.Span[4];
        if (session.Friends.PendingFrom != requesterId)
        {
            logger.LogWarning("[{Session}] {Name}: friend answer for {Id} without a request", session.Id, character.Name, requesterId);
            return;
        }
        session.Friends.PendingFrom = 0;
        if (world.SessionOf(requesterId) is not { } requester || requester.ActiveCharacter is not { } other)
            return;
        if (!requester.TryExclusiveFrom(session, out var held)) // the requester's own packet may be changing or saving their list
        {
            logger.LogWarning("[{Session}] {Name}: friend answer to {Other} refused: the requester's session is busy", session.Id, character.Name,
                other.Name);
            session.Send(FriendMessages.ListFull()); // the answer the client gets when the friendship cannot be made
            requester.Send(FriendMessages.ListFull());
            return;
        }
        using (held)
            await AnswerAsync(session, character, requester, other, answer).ConfigureAwait(false);
    }

    /// <summary>Carries out the answer to a friend request under the requester's session gate.</summary>
    /// <param name="session">The answering player's session.</param>
    /// <param name="character">The answering player's character.</param>
    /// <param name="requester">The requester's session, whose gate the caller holds.</param>
    /// <param name="other">The requester's character.</param>
    /// <param name="answer">The answer: 1 yes, 2 no.</param>
    /// <returns>A task that completes when both are saved.</returns>
    private async Task AnswerAsync(GameSession session, Character character, GameSession requester, Character other, byte answer)
    {
        var requesterId = requester.Account!.LoginId;
        var myId = session.Account!.LoginId;
        if (answer == 1 && (character.Friends.Count >= FriendEntry.MaxFriends || other.Friends.Count >= FriendEntry.MaxFriends))
        {
            logger.LogWarning("[{Session}] {Name}: friend answer to {Other} refused, a friend list is full", session.Id, character.Name, other.Name);
            session.Send(FriendMessages.ListFull());
            requester.Send(FriendMessages.ListFull());
            return;
        }
        if (answer == 1 && !character.Friends.Exists(f => f.Id == requesterId) && !other.Friends.Exists(f => f.Id == myId))
        {
            var mine = new FriendEntry { Id = requesterId };
            Refresh(mine, other);
            character.Friends.Add(mine);
            var theirs = new FriendEntry { Id = myId };
            Refresh(theirs, character);
            other.Friends.Add(theirs);
            session.Send(FriendMessages.Entry(Info(mine)));
            await store.SaveAsync(session.Account).ConfigureAwait(false);
            await store.SaveAsync(requester.Account!).ConfigureAwait(false);
        }
        requester.Send(FriendMessages.Answer(myId, answer));
        logger.LogInformation("[{Session}] {Name} answers {Other}'s friend request: {Answer}", session.Id, character.Name, other.Name,
            answer == 1 ? "yes" : answer == 2 ? "no" : "no answer");
    }

    /// <summary>
    /// Handles deletion of a friend, removing the entry on both sides; an online friend's list changes under their session gate, and a busy friend
    /// session refuses the deletion.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: the friend's login id.</param>
    internal async ValueTask OnDelete(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (InWorld(session) is not { } character || body.Length < 4)
            return;
        var friendId = BinaryPrimitives.ReadInt32LittleEndian(body.Span);
        if (!character.Friends.Exists(f => f.Id == friendId))
            return;
        var other = world.SessionOf(friendId);
        var held = default(GameSession.Exclusive);
        if (other is not null && !other.TryExclusiveFrom(session, out held)) // the friend's own packet may be changing or saving their list
        {
            logger.LogWarning("[{Session}] {Name}: delete friend {Id} refused: the friend's session is busy", session.Id, character.Name, friendId);
            return;
        }
        using (held)
        {
            character.Friends.RemoveAll(f => f.Id == friendId);
            session.Send(FriendMessages.Removed(friendId));
            var myId = session.Account!.LoginId;
            if (await store.FindByLoginIdAsync(friendId).ConfigureAwait(false) is { } account)
            {
                foreach (var c in account.Characters)
                    c.Friends.RemoveAll(f => f.Id == myId);
                other?.Send(FriendMessages.Removed(myId));
                await store.SaveAsync(account).ConfigureAwait(false);
            }
        }
        await store.SaveAsync(session.Account!).ConfigureAwait(false);
        logger.LogInformation("[{Session}] {Name} deletes friend {Id}", session.Id, character.Name, friendId);
    }

    /// <summary>Takes the session's turn to send a mail: refused while <see cref="MailWait"/> has not passed since its last mail.</summary>
    /// <param name="session">The sender's session.</param>
    /// <returns>True when the session may send now; its last mail time is then set.</returns>
    public static bool TakeMailTurn(GameSession session)
    {
        var now = GameClock.UtcNow;
        if (now - session.Friends.LastMailUtc < MailWait)
            return false;
        session.Friends.LastMailUtc = now;
        return true;
    }

    /// <summary>
    /// Handles a mail to one or more friends: the text must fit the client's form, the session must have waited <see cref="MailWait"/> since its
    /// last mail, and each recipient must be on the sender's friend list.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: a count, that many login ids, then the text.</param>
    internal async ValueTask OnMail(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (InWorld(session) is not { } character || body.Length < 1)
            return;
        var count = body.Span[0];
        if (body.Length < 1 + 4 * count)
            return;
        var text = GameEncoding.GetString(body.Span[(1 + 4 * count)..]);
        if (text.Length == 0 || text.Length > MaxMailText || count > FriendEntry.MaxFriends)
        {
            logger.LogWarning("[{Session}] {Name}: mail refused ({Count} recipients, {Length} characters)", session.Id, character.Name, count,
                text.Length);
            return;
        }
        if (!TakeMailTurn(session))
        {
            logger.LogWarning("[{Session}] {Name}: mail refused, sent too soon after the last one", session.Id, character.Name);
            return;
        }
        var targets = new HashSet<int>();
        for (var i = 0; i < count; i++)
            targets.Add(BinaryPrimitives.ReadInt32LittleEndian(body.Span[(1 + 4 * i)..]));
        var myId = session.Account!.LoginId;
        foreach (var targetId in targets)
        {
            if (!character.Friends.Exists(f => f.Id == targetId))
            {
                logger.LogWarning("[{Session}] {Name}: mail to {Id}, who is not a friend", session.Id, character.Name, targetId);
                continue;
            }
            if (!await DeliverAsync(myId, targetId, text, session).ConfigureAwait(false))
            {
                logger.LogWarning("[{Session}] {Name}: mail to unknown player {Id}", session.Id, character.Name, targetId);
                continue;
            }
            logger.LogInformation("[{Session}] {Name} mails {Id}: {Text}", session.Id, character.Name, targetId, text);
        }
    }

    /// <summary>
    /// Delivers a mail: straight to an online player, or into the mailbox of the first character of an offline account. A block list entry refuses
    /// it, and a mailbox that holds <see cref="MaxMailbox"/> mails drops it.
    /// </summary>
    /// <param name="senderId">The sender's login id.</param>
    /// <param name="targetId">The recipient's login id.</param>
    /// <param name="text">The mail text.</param>
    /// <param name="sender">The sender's session; null skips the block check and the refusal message.</param>
    /// <returns>False when the recipient does not exist, true otherwise (also when blocked or the mailbox is full).</returns>
    public async Task<bool> DeliverAsync(int senderId, int targetId, string text, GameSession? sender = null)
    {
        var time = GameClock.Now.ToOADate();
        if (world.SessionOf(targetId) is { } online)
        {
            if (sender is not null && BlocklistHandlers.Blocks(online.ActiveCharacter, senderId))
            {
                BlocklistHandlers.Refuse(sender, 2);
                return true;
            }
            online.Send(FriendMessages.Mail(senderId, time, text));
            return true;
        }
        if (await store.FindByLoginIdAsync(targetId).ConfigureAwait(false) is not { } account || account.Characters.FirstOrDefault() is not { } offline)
            return false;
        if (sender is not null && BlocklistHandlers.Blocks(offline, senderId))
        {
            BlocklistHandlers.Refuse(sender, 2);
            return true;
        }
        if (offline.Mailbox.Count >= MaxMailbox)
        {
            logger.LogWarning("Mail from {Sender} to {Target} dropped: the mailbox is full", senderId, targetId);
            return true;
        }
        offline.Mailbox.Add(new Mail { SenderId = senderId, Time = time, Text = text });
        await store.SaveAsync(account).ConfigureAwait(false);
        return true;
    }

    /// <summary>Copies a character's current name, level, job and looks into a friend entry.</summary>
    /// <param name="f">The friend entry to update.</param>
    /// <param name="c">The friend's character.</param>
    private static void Refresh(FriendEntry f, Character c)
    {
        f.Name = c.Name;
        f.Level = c.Level;
        f.Job = c.Job;
        f.Reborn = c.RebornCount;
        f.Element = c.Element;
        f.BodyType = c.BodyType;
        f.Head = c.Head;
        f.Nickname = c.Nickname;
        f.Colour1 = c.Colour1;
        f.Colour2 = c.Colour2;
    }

    /// <summary>Builds the packet record of a friend entry.</summary>
    /// <param name="f">The friend entry.</param>
    /// <returns>The friend data to send.</returns>
    private static FriendInfo Info(FriendEntry f) =>
        new(f.Id, f.Name, f.Level, f.Job, f.Reborn, f.Element, f.BodyType, f.Head, f.Nickname, f.Colour1, f.Colour2);

    /// <summary>Gives the active character of a session that is in the world.</summary>
    /// <param name="session">The player's session.</param>
    /// <returns>The character, or null when the session is not in the world.</returns>
    private static Character? InWorld(GameSession session)
    {
        var character = session.ActiveCharacter;
        return session.State == SessionState.InWorld && character is not null && session.Account is not null ? character : null;
    }
}
