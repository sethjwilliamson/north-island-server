using System.Buffers.Binary;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NorthIsland.Protocol.Messages.Player;
using NorthIsland.Protocol.Messages.World;
using NorthIsland.Protocol.Text;

namespace NorthIsland.Server.Features.Guilds;

/// <summary>The guild actions: founding, inviting, joining, leaving, posts, rules, badges and the periodic dismissal check.</summary>
/// <param name="guilds">The guild registry.</param>
/// <param name="world">The world state, to find sessions.</param>
/// <param name="castles">The castle registry; the Trojan War blocks some requests.</param>
/// <param name="boards">The notice boards, whose guild board goes with a removed guild.</param>
/// <param name="options">The guild options.</param>
/// <param name="logger">The logger.</param>
public sealed class GuildHandlers(
    GuildRegistry guilds,
    WorldState world,
    CastleRegistry castles,
    BoardRegistry boards,
    IOptions<GuildFeatureOptions> options,
    ILogger<GuildHandlers> logger)
{
    /// <summary>When each guild last uploaded a badge (UTC), by guild id.</summary>
    private readonly ConcurrentDictionary<uint, DateTime> _badgeUploads = new();

    /// <summary>The longest guild name, in characters.</summary>
    public const int MaxNameLength = 14;

    /// <summary>The level a player needs to found a guild.</summary>
    public const int FoundingLevel = 20;

    /// <summary>The gold that founding a guild costs.</summary>
    public const int FoundingCost = 10000;

    /// <summary>Hooks the world events: sends guild tags to players who meet, and tells the guild when a member leaves the world.</summary>
    internal void Start()
    {
        world.PlayersMet += (a, b) =>
        {
            if (TagOf(a) is { } ta)
                b.Send(GuildMessages.Tags([ta]));
            if (TagOf(b) is { } tb)
                a.Send(GuildMessages.Tags([tb]));
        };
        world.Leaving += s =>
        {
            if (s.Account is { } account && s.ActiveCharacter is not null && guilds.Of(account.LoginId) is { } guild)
                SendMemberStates(s, guild, account.LoginId, online: false);
        };
    }

    /// <summary>Sends the guild badge versions to a session.</summary>
    /// <param name="session">The session.</param>
    /// <param name="castleToo">True to send the castle owner packet as well.</param>
    public void SendBadgeList(GameSession session, bool castleToo = true)
    {
        var listed = guilds.All().Where(g => g.Badge is not null).Select(g => (g.Id, g.BadgeVersion)).ToList();
        if (listed.Count == 0)
            return;
        session.Send(GuildMessages.BadgeList(listed));
        if (castleToo)
            session.Send(castles.OwnerPacket());
    }

    /// <summary>
    /// Handles <c>39/18</c>: a member with the right changes the guild badge; the guild's members get their own badge and everybody else the new
    /// image. A guild may upload once per <see cref="GuildOptions.BadgeUploadSeconds"/>.
    /// </summary>
    /// <param name="session">The sender's session.</param>
    /// <param name="body">The badge image bytes.</param>
    internal ValueTask OnUploadBadge(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (InWorld(session) is not { } character)
            return ValueTask.CompletedTask;
        var id = session.Account!.LoginId;
        if (body.Length != GuildMessages.BadgeSize || guilds.Of(id) is not { } guild || !guild.CanChangeBadge(id))
        {
            logger.LogWarning("[{Session}] {Name}: guild badge upload refused ({Length} bytes)", session.Id, character.Name, body.Length);
            return ValueTask.CompletedTask;
        }
        var now = GameClock.UtcNow;
        var wait = TimeSpan.FromSeconds(options.Value.Guilds.BadgeUploadSeconds);
        if (_badgeUploads.TryGetValue(guild.Id, out var last) && now - last < wait)
        {
            logger.LogWarning("[{Session}] {Name}: guild badge upload refused, guild '{Guild}' uploaded one {Seconds:0} s ago", session.Id,
                character.Name, guild.Name, (now - last).TotalSeconds);
            return ValueTask.CompletedTask;
        }
        _badgeUploads[guild.Id] = now;
        var version = guilds.SetBadge(guild, body.ToArray());
        var members = Online(guild).ToList();
        foreach (var s in members)
            s.Send(GuildMessages.OwnBadge(version, guild.Badge!));
        var push = GuildMessages.Badges([(guild.Id, version, guild.Badge!)]);
        foreach (var s in world.OnlineSessions().Except(members))
            s.Send(push);
        if (castles.Owner == guild)
            foreach (var s in world.OnlineSessions())
                s.Send(castles.OwnerPacket());
        logger.LogInformation("[{Session}] {Name} uploads guild '{Guild}' badge version {Version}", session.Id, character.Name, guild.Name, version);
        return ValueTask.CompletedTask;
    }

    /// <summary>Handles <c>39/19</c>: answers a request for badge images, for up to ten guilds.</summary>
    /// <param name="session">The sender's session.</param>
    /// <param name="body">The guild ids wanted, four bytes each.</param>
    internal ValueTask OnBadgeRequest(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.State != SessionState.InWorld || body.Length < 4)
            return ValueTask.CompletedTask;
        List<(uint, byte, byte[])> found = [];
        for (var at = 0; at + 4 <= body.Length && found.Count < 10; at += 4)
            if (guilds.ById(BinaryPrimitives.ReadUInt32LittleEndian(body.Span[at..])) is { Badge: not null } guild)
                found.Add((guild.Id, guild.BadgeVersion, guild.Badge));
        if (found.Count > 0)
            session.Send(GuildMessages.Badges(found));
        return ValueTask.CompletedTask;
    }

    /// <summary>The id of the guild a player belongs to.</summary>
    /// <param name="playerId">The player's login id.</param>
    /// <returns>The guild id, or 0 when the player is in no guild.</returns>
    public uint IdOf(int playerId) => guilds.Of(playerId)?.Id ?? 0;

    /// <summary>Sends a player their guild at login: info, tag and the online state of the members.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="character">The player's character, which refreshes their member entry.</param>
    public void SendOnLogin(GameSession session, Character character)
    {
        var id = session.Account!.LoginId;
        if (guilds.Of(id) is not { } guild)
            return;
        lock (guilds.Lock)
            guild.Member(id)?.Refresh(character);
        session.Send(Info(guild));
        session.Send(GuildMessages.Tags([(id, guild.Id, guild.Name)]));
        SendMemberStates(session, guild, id);
        NoteOnline(guild);
    }

    /// <summary>Remembers that enough members were online together once, once at least the configured number of them is online.</summary>
    /// <param name="guild">The guild.</param>
    private void NoteOnline(Guild guild)
    {
        if (guild.EnoughOnline || Online(guild).Count() < options.Value.Guilds.OnlineMembers)
            return;
        guild.EnoughOnline = true;
        guilds.Save();
    }

    /// <summary>Tells the guild a member went online or offline and, on login, sends that member the state of the others.</summary>
    /// <param name="session">The member's session.</param>
    /// <param name="guild">The guild.</param>
    /// <param name="id">The member's login id.</param>
    /// <param name="online">True when the member comes online, false when they leave.</param>
    private void SendMemberStates(GameSession session, Guild guild, int id, bool online = true)
    {
        List<int> ids;
        lock (guilds.Lock)
            ids = [.. guild.Members.Select(m => m.Id)];
        if (online)
            session.Send(GuildMessages.MemberStates(ids.Select(m => (m, m == id || world.IsOnline(m)))));
        var packet = GuildMessages.MemberStates([(id, online)]);
        foreach (var other in Online(guild).Where(s => s != session))
            other.Send(packet);
    }

    /// <summary>Opens the create-guild form on the client.</summary>
    /// <param name="session">The session.</param>
    public void OpenCreateForm(GameSession session)
    {
        session.Send(GuildMessages.OpenCreate());
        logger.LogInformation("[{Session}] opens the create-guild form", session.Id);
    }

    /// <summary>Sends a guild chat line to the online members of the sender's guild.</summary>
    /// <param name="session">The sender's session.</param>
    /// <param name="channel">The chat channel byte.</param>
    /// <param name="text">The chat text.</param>
    public void Chat(GameSession session, byte channel, string text)
    {
        var id = session.Account!.LoginId;
        if (guilds.Of(id) is not { } guild)
            return;
        var packet = WorldServerMessages.Chat(channel, id, text);
        foreach (var member in Online(guild))
            member.Send(packet);
    }

    /// <summary>
    /// Handles <c>39/1</c>: founds a guild when the name, level, gold, guild limit and name checks pass, and answers with a result code. The
    /// membership, limit and name checks run in the registry together with the founding. Notices left on the new id's board are deleted.
    /// </summary>
    /// <param name="session">The founder's session.</param>
    /// <param name="body">The guild name.</param>
    internal ValueTask OnCreate(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (InWorld(session) is not { } character)
            return ValueTask.CompletedTask;
        if (true && WarBlocks(session, 5)) // variant
            return ValueTask.CompletedTask;
        var id = session.Account!.LoginId;
        var name = GameEncoding.GetString(body.Span).Trim();
        Guild? guild = null;
        byte result = name.Length is 0 or > MaxNameLength ? (byte)1 // result: bad name
            : character.Level < FoundingLevel || character.Gold < FoundingCost ? (byte)2 // result: level or gold too low
                : (byte)0;
        if (result == 0)
            (guild, result) = guilds.Create(name, character, id, options.Value.Guilds.MaxGuilds);
        session.Send(GuildMessages.CreateResult(result));
        if (guild is null)
        {
            logger.LogInformation("[{Session}] {Name}: guild '{Guild}' refused ({Result})", session.Id, character.Name, name, result);
            return ValueTask.CompletedTask;
        }
        character.Gold -= FoundingCost;
        session.Send(PlayerMessages.MoneyChange(-FoundingCost));
        boards.RemoveBoard(guild.Id);
        session.Send(Info(guild));
        SendTag(session, guild);
        logger.LogInformation("[{Session}] {Name} founds guild '{Guild}' ({Id})", session.Id, character.Name, guild.Name, guild.Id);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Handles <c>39/2</c>: invites a player on the same map into the guild if the sender may invite; a refusal is answered with a result.
    /// </summary>
    /// <param name="session">The inviter's session.</param>
    /// <param name="body">The invited player's id.</param>
    internal ValueTask OnInvite(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (InWorld(session) is not { } character || body.Length < 4)
            return ValueTask.CompletedTask;
        var id = session.Account!.LoginId;
        var targetId = BinaryPrimitives.ReadInt32LittleEndian(body.Span);
        var guild = guilds.Of(id);
        string? refusal = guild is null ? "not in a guild"
            : !guild.CanInvite(id) ? "no invite right"
                : guild.Members.Count >= Guild.MaxMembers ? "the guild is full"
                    : world.SessionOf(targetId) is not { } target || target.ActiveCharacter?.MapId != character.MapId ? "target not here"
                        : guilds.Of(targetId) is not null ? "target has a guild"
                            : null;
        if (refusal is not null)
        {
            session.Send(GuildMessages.InviteResult(targetId, 4)); // result
            logger.LogInformation("[{Session}] {Name}: guild invite to {Target} refused ({Why})", session.Id, character.Name, targetId, refusal);
            return ValueTask.CompletedTask;
        }
        var invited = world.SessionOf(targetId)!;
        invited.GuildOffers.InvitedBy = id;
        invited.Send(GuildMessages.Invitation(id));
        logger.LogInformation("[{Session}] {Name} invites {Target} to guild '{Guild}'", session.Id, character.Name, invited.ActiveCharacter?.Name, guild!.Name);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Handles <c>39/3</c>: the answer of an invited player; on yes they join the guild, if it still has room and they are in no guild (checked
    /// under the registry's lock), and the inviter and the guild are told.
    /// </summary>
    /// <param name="session">The invited player's session.</param>
    /// <param name="body">The inviter's id and the answer byte.</param>
    internal ValueTask OnAnswer(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (InWorld(session) is not { } character || body.Length < 5)
            return ValueTask.CompletedTask;
        if (body.Span[4] == 1 && WarBlocks(session, 6)) // 4 answer, 6 variant
            return ValueTask.CompletedTask;
        var id = session.Account!.LoginId;
        var inviterId = BinaryPrimitives.ReadInt32LittleEndian(body.Span);
        var answer = body.Span[4];
        if (session.GuildOffers.InvitedBy != inviterId)
            return ValueTask.CompletedTask;
        session.GuildOffers.InvitedBy = 0;
        var inviter = world.SessionOf(inviterId)!;
        var guild = guilds.Of(inviterId);
        var member = new GuildMember { Id = id };
        member.Refresh(character);
        if (answer != 1 || guild is null || !guilds.TryJoin(guild, member))
        {
            inviter?.Send(GuildMessages.InviteResult(id, answer == 1 ? (byte)4 : answer)); // result
            return ValueTask.CompletedTask;
        }
        inviter?.Send(GuildMessages.InviteResult(id, 1));
        foreach (var other in Online(guild).Where(s => s != session))
            other.Send(GuildMessages.MemberJoined(id));
        session.Send(Info(guild));
        session.Send(GuildMessages.Welcome());
        SendTag(session, guild);
        NoteOnline(guild);
        logger.LogInformation("[{Session}] {Name} joins guild '{Guild}'", session.Id, character.Name, guild.Name);
        return ValueTask.CompletedTask;
    }

    /// <summary>Refuses a request while the Trojan War is on.</summary>
    /// <param name="session">The session to tell.</param>
    /// <param name="variant">The refusal number, which says which request was refused.</param>
    /// <returns>True when the war is on and the refusal was sent.</returns>
    private bool WarBlocks(GameSession session, byte variant)
    {
        if (!castles.WarOn)
            return false;
        session.Send(GuildMessages.WarRefused(variant));
        return true;
    }

    /// <summary>Handles <c>39/6</c>: a member leaves their guild; the lord cannot.</summary>
    /// <param name="session">The member's session.</param>
    /// <param name="body">The packet body, not used.</param>
    internal ValueTask OnLeave(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (InWorld(session) is not { } character)
            return ValueTask.CompletedTask;
        if (true && WarBlocks(session, 1))
            return ValueTask.CompletedTask;
        var id = session.Account!.LoginId;
        if (guilds.Of(id) is not { } guild)
        {
            session.Send(GuildMessages.LeaveResult(1, id));
            return ValueTask.CompletedTask;
        }
        if (guild.LeaderId == id)
        {
            session.Send(GuildMessages.LeaveResult(2, id)); // result
            return ValueTask.CompletedTask;
        }
        Remove(guild, id);
        logger.LogInformation("[{Session}] {Name} leaves guild '{Guild}'", session.Id, character.Name, guild.Name);
        return ValueTask.CompletedTask;
    }

    /// <summary>Handles <c>39/7</c>: the lord, or a vice for a member who is not one, removes a member from the guild.</summary>
    /// <param name="session">The sender's session.</param>
    /// <param name="body">The id of the member to remove.</param>
    internal ValueTask OnKick(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (InWorld(session) is not { } character || body.Length < 4)
            return ValueTask.CompletedTask;
        if (true && WarBlocks(session, 2)) // variant
            return ValueTask.CompletedTask;
        var id = session.Account!.LoginId;
        var memberId = BinaryPrimitives.ReadInt32LittleEndian(body.Span);
        if (guilds.Of(id) is not { } guild || guild.Member(memberId) is null)
        {
            session.Send(GuildMessages.LeaveResult(1, memberId));
            return ValueTask.CompletedTask;
        }
        var allowed = memberId != guild.LeaderId && memberId != id
            && (id == guild.LeaderId || guild.Vices.Contains(id) && !guild.Vices.Contains(memberId));
        if (!allowed)
        {
            session.Send(GuildMessages.LeaveResult(3, memberId)); // result
            return ValueTask.CompletedTask;
        }
        Remove(guild, memberId);
        logger.LogInformation("[{Session}] {Name} removes {Member} from guild '{Guild}'", session.Id, character.Name, memberId, guild.Name);
        return ValueTask.CompletedTask;
    }

    /// <summary>Handles <c>39/8</c>: the lord disbands the guild.</summary>
    /// <param name="session">The sender's session.</param>
    /// <param name="body">The packet body, not used.</param>
    internal ValueTask OnDisband(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (InWorld(session) is not { } character)
            return ValueTask.CompletedTask;
        if (true && WarBlocks(session, 3)) // variant
            return ValueTask.CompletedTask;
        var id = session.Account!.LoginId;
        if (guilds.Of(id) is not { } guild || guild.LeaderId != id)
        {
            session.Send(GuildMessages.Disbanded(1));
            return ValueTask.CompletedTask;
        }
        Dissolve(guild);
        logger.LogInformation("[{Session}] {Name} disbands guild '{Guild}'", session.Id, character.Name, guild.Name);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Removes a guild: its online members are told and lose their tag, its board's notices are deleted, its badge wait is forgotten, and the castle
    /// loses its owner if this guild owned it.
    /// </summary>
    /// <param name="guild">The guild to remove.</param>
    private void Dissolve(Guild guild)
    {
        var online = Online(guild).ToList();
        guilds.Remove(guild);
        boards.RemoveBoard(guild.Id);
        _badgeUploads.TryRemove(guild.Id, out _);
        if (castles.GuildGone(guild.Id) is { } noOwner)
        {
            var silent = castles.OwnerPacket();
            foreach (var s in world.OnlineSessions())
                s.Send(s.ActiveCharacter is { } c && CastleRegistry.IsTrojanRoom(c.MapId) ? noOwner : silent);
        }
        foreach (var s in online)
        {
            s.Send(GuildMessages.Disbanded(0));
            ClearTag(s);
        }
    }

    /// <summary>Dismisses the guilds that missed the recruiting or online-together checks of the guild options.</summary>
    /// <param name="now">The current time.</param>
    /// <returns>The names of the guilds that were dismissed.</returns>
    public List<string> Sweep(DateTime now)
    {
        var o = options.Value.Guilds;
        List<string> dismissed = [];
        if (!o.AutoDismiss)
            return dismissed;
        foreach (var guild in guilds.All())
        {
            string? why = null;
            var changed = false;
            lock (guilds.Lock)
            {
                if (guild.RulesSince == 0)
                {
                    guild.RulesSince = now.ToOADate();
                    guilds.Save();
                    continue;
                }
                foreach (var m in guild.Members)
                {
                    if (world.SessionOf(m.Id) is { } s && s.ActiveCharacter is { } c)
                        m.Refresh(c);
                }
                var since = DateTime.FromOADate(guild.RulesSince);
                if (!guild.RecruitChecked && now >= since.AddDays(o.RecruitDays))
                {
                    var qualified = guild.Members.Count(m => m.Level >= o.RecruitLevel);
                    if (qualified < o.RecruitMembers)
                        why = $"{qualified} of {o.RecruitMembers} members of level {o.RecruitLevel} after {o.RecruitDays} days";
                    else
                        (guild.RecruitChecked, changed) = (true, true);
                }
                if (why is null && !guild.EnoughOnline && now >= since.AddDays(o.OnlineDays))
                    why = $"never {o.OnlineMembers} members online together in {o.OnlineDays} days";
            }
            if (why is not null)
            {
                Dissolve(guild);
                dismissed.Add(guild.Name);
                logger.LogInformation("Guild '{Guild}' ({Id}) is dismissed: {Why}", guild.Name, guild.Id, why);
            }
            else if (changed)
                guilds.Save();
        }
        return dismissed;
    }

    /// <summary>Handles <c>39/9</c>: the lord sets the guild rules (at most 200 characters) and the online members are sent them.</summary>
    /// <param name="session">The lord's session.</param>
    /// <param name="body">The rules text.</param>
    internal ValueTask OnRules(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (InWorld(session) is null)
            return ValueTask.CompletedTask;
        var id = session.Account!.LoginId;
        if (guilds.Of(id) is not { } guild || guild.LeaderId != id)
            return ValueTask.CompletedTask;
        var rules = GameEncoding.GetString(body.Span);
        lock (guilds.Lock)
            guild.Rules = rules.Length > 200 ? rules[..200] : rules;
        guilds.Save();
        foreach (var s in Online(guild))
            s.Send(GuildMessages.RulesChanged(guild.Rules));
        return ValueTask.CompletedTask;
    }

    /// <summary>Handles the lord's offer of a post to a member who is online: the vice post (<c>39/10</c>) or the lord post (<c>39/12</c>).</summary>
    /// <param name="session">The lord's session.</param>
    /// <param name="body">The id of the member who is offered the post.</param>
    /// <param name="lord">True for the lord post, false for the vice post.</param>
    internal ValueTask OnOffer(GameSession session, ReadOnlyMemory<byte> body, bool lord)
    {
        if (InWorld(session) is not { } character || body.Length < 4)
            return ValueTask.CompletedTask;
        if (true && WarBlocks(session, 7)) // variant
            return ValueTask.CompletedTask;
        var id = session.Account!.LoginId;
        var targetId = BinaryPrimitives.ReadInt32LittleEndian(body.Span);
        byte? error = guilds.Of(id) is not { } guild || guild.LeaderId != id || guild.Member(targetId) is null || targetId == id ? (byte)7
            : !lord && (guild.Vices.Contains(targetId) || guild.Vices.Count >= GuildMessages.ViceSlots) ? (byte)4 // error
                : world.SessionOf(targetId) is null ? (byte)8 // error
                    : null;
        if (error is { } code)
        {
            session.Send(GuildMessages.PositionError(code));
            return ValueTask.CompletedTask;
        }
        var target = world.SessionOf(targetId)!;
        target.GuildOffers.OfferedPost = (id, lord);
        target.Send(GuildMessages.Offer(id, lord));
        logger.LogInformation("[{Session}] {Name} offers the {Post} post to {Target}", session.Id, character.Name, lord ? "lord" : "vice", targetId);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Handles the answer to a post offer (<c>39/13</c> for the lord post, <c>39/14</c> for the vice post); on yes the post is handed over.
    /// </summary>
    /// <param name="session">The answering session.</param>
    /// <param name="body">The offerer's id and the answer byte.</param>
    /// <param name="lord">True when the answer is for the lord post.</param>
    internal ValueTask OnPostAnswer(GameSession session, ReadOnlyMemory<byte> body, bool lord)
    {
        if (InWorld(session) is not { } character || body.Length < 5)
            return ValueTask.CompletedTask;
        var id = session.Account!.LoginId;
        var otherId = BinaryPrimitives.ReadInt32LittleEndian(body.Span);
        var yes = body.Span[4] == 1;
        if (guilds.Of(id) is not { } guild)
            return ValueTask.CompletedTask;

        if (!lord && guild.LeaderId == id && session.GuildOffers.OfferedPost is null)
        {
            if (guild.Member(otherId) is not null && !guild.Vices.Contains(otherId) && otherId != id && guild.Vices.Count < GuildMessages.ViceSlots)
                Appoint(guild, otherId);
            return ValueTask.CompletedTask;
        }
        if (session.GuildOffers.OfferedPost is not { } post || post.FromId != otherId || post.Lord != lord)
            return ValueTask.CompletedTask;
        session.GuildOffers.OfferedPost = null;
        var offerer = world.SessionOf(otherId)!;
        if (!yes || guild.LeaderId != otherId)
        {
            offerer?.Send(GuildMessages.PositionError(lord ? (byte)11 : (byte)9)); // code
            return ValueTask.CompletedTask;
        }
        if (lord)
        {
            lock (guilds.Lock)
            {
                guild.LeaderId = id;
                guild.Vices.Remove(id);
                (guild.Member(id)!.Rights, guild.Member(otherId)!.Rights) = ([1, 1, 1, 1, 1], new byte[5]);
            }
            guilds.Save();
            foreach (var s in Online(guild))
                s.Send(GuildMessages.NewLord(id));
        }
        else if (guild.Vices.Count < GuildMessages.ViceSlots && !guild.Vices.Contains(id))
        {
            Appoint(guild, id);
        }
        offerer?.Send(GuildMessages.PostAccepted(id, lord));
        logger.LogInformation("[{Session}] {Name} becomes {Post} of guild '{Guild}'", session.Id, character.Name, lord ? "lord" : "vice lord", guild.Name);
        return ValueTask.CompletedTask;
    }

    /// <summary>Makes a member a vice, gives them the second right and tells the online members.</summary>
    /// <param name="guild">The guild.</param>
    /// <param name="memberId">The member's login id.</param>
    private void Appoint(Guild guild, int memberId)
    {
        lock (guilds.Lock)
        {
            guild.Vices.Add(memberId);
            guild.Member(memberId)!.Rights[1] = 1;
        }
        guilds.Save();
        foreach (var s in Online(guild))
            s.Send(GuildMessages.ViceChanged(memberId, appointed: true));
    }

    /// <summary>Handles <c>39/11</c>: the lord takes the vice post from a member.</summary>
    /// <param name="session">The lord's session.</param>
    /// <param name="body">The vice's id.</param>
    internal ValueTask OnDepose(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (InWorld(session) is null || body.Length < 4)
            return ValueTask.CompletedTask;
        if (true && WarBlocks(session, 8)) // variant
            return ValueTask.CompletedTask;
        var id = session.Account!.LoginId;
        var viceId = BinaryPrimitives.ReadInt32LittleEndian(body.Span);
        if (guilds.Of(id) is not { } guild || guild.LeaderId != id || !guild.Vices.Contains(viceId))
        {
            session.Send(GuildMessages.PositionError(7)); // code
            return ValueTask.CompletedTask;
        }
        lock (guilds.Lock)
        {
            guild.Vices.Remove(viceId);
            guild.Member(viceId)!.Rights[1] = 0;
        }
        guilds.Save();
        foreach (var s in Online(guild))
            s.Send(GuildMessages.ViceChanged(viceId, appointed: false));
        return ValueTask.CompletedTask;
    }

    /// <summary>Takes a member out of a guild, tells them and the guild, and removes their tag.</summary>
    /// <param name="guild">The guild.</param>
    /// <param name="memberId">The login id of the member who leaves.</param>
    private void Remove(Guild guild, int memberId)
    {
        lock (guilds.Lock)
        {
            guild.Members.RemoveAll(m => m.Id == memberId);
            guild.Vices.Remove(memberId);
        }
        guilds.Save();
        if (world.SessionOf(memberId) is { } gone)
        {
            gone.Send(GuildMessages.LeaveResult(0, memberId));
            ClearTag(gone);
        }
        foreach (var s in Online(guild))
            s.Send(GuildMessages.MemberRemoved(memberId));
    }

    /// <summary>The tag of a player's guild.</summary>
    /// <param name="s">The session.</param>
    /// <returns>The player's login id, guild id and guild name, or null when the player is in no guild.</returns>
    private (int, uint, string)? TagOf(GameSession s) =>
        s.Account is { } a && guilds.Of(a.LoginId) is { } g ? (a.LoginId, g.Id, g.Name) : null;

    /// <summary>Sends a guild tag to the player and to the players on their map.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="guild">The player's guild.</param>
    private void SendTag(GameSession session, Guild guild)
    {
        var packet = GuildMessages.Tags([(session.Account!.LoginId, guild.Id, guild.Name)]);
        session.Send(packet);
        world.Broadcast(session, packet, sameMapOnly: true);
    }

    /// <summary>Clears a player's guild tag for them and the players on their map.</summary>
    /// <param name="session">The player's session.</param>
    private void ClearTag(GameSession session)
    {
        var packet = GuildMessages.Tags([(session.Account!.LoginId, 0u, "")]);
        session.Send(packet);
        world.Broadcast(session, packet, sameMapOnly: true);
    }

    /// <summary>The sessions of the guild's members that are online.</summary>
    /// <param name="guild">The guild.</param>
    /// <returns>The online sessions of its members.</returns>
    private IEnumerable<GameSession> Online(Guild guild)
    {
        List<int> ids;
        lock (guilds.Lock)
            ids = [.. guild.Members.Select(m => m.Id)];
        foreach (var id in ids)
        {
            if (world.SessionOf(id) is { } s)
                yield return s;
        }
    }

    /// <summary>Builds the guild info packet with the lord, the vices and the other members.</summary>
    /// <param name="guild">The guild.</param>
    /// <returns>The packet.</returns>
    private byte[] Info(Guild guild)
    {
        lock (guilds.Lock)
        {
            var lord = guild.Member(guild.LeaderId)!;
            var vices = guild.Vices.Select(guild.Member).OfType<GuildMember>().Take(GuildMessages.ViceSlots).ToList();
            var rest = guild.Members.Where(m => m != lord && !vices.Contains(m)).ToList();
            return GuildMessages.Info(guild.Name, lord.Info(), [.. vices.Select(v => v.Info())], [.. rest.Select(m => m.Info())],
                guild.Rules, guild.Founded);
        }
    }

    /// <summary>The active character of a session that is in the world.</summary>
    /// <param name="session">The session.</param>
    /// <returns>The character, or null when the session is not in the world.</returns>
    private static Character? InWorld(GameSession session)
    {
        var character = session.ActiveCharacter;
        return session.State == SessionState.InWorld && character is not null && session.Account is not null ? character : null;
    }
}
