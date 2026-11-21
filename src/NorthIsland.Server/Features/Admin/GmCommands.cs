using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging;
using NorthIsland.GameData.Formulas;
using NorthIsland.Protocol.IO;
using NorthIsland.Protocol.Messages.Items;
using NorthIsland.Protocol.Messages.Login;
using NorthIsland.Protocol.Messages.Player;
using NorthIsland.Protocol.Opcodes;

namespace NorthIsland.Server.Features.Admin;

/// <summary>The slash commands of GMs (<c>/op</c>, <c>/kick</c>, <c>/ban</c>, <c>/give</c>, <c>/level</c>, <c>/tp</c>, ...) typed in the chat.</summary>
/// <param name="world">The world state, used to find online players and to move them.</param>
/// <param name="store">The account store, to find and save accounts.</param>
/// <param name="bans">The ban list.</param>
/// <param name="sessions">The live session of each account, to disconnect a banned account that is at character select.</param>
/// <param name="items">The item records for <c>/give</c>.</param>
/// <param name="formulas">The formula tables, for the experience of a level.</param>
/// <param name="combat">The combat stats, recalculated after a level change.</param>
/// <param name="experience">Applies level-ups.</param>
/// <param name="maps">The map data, to check teleport targets.</param>
/// <param name="logger">Logger for the GM actions.</param>
public sealed class GmCommands(
    WorldState world,
    IAccountStore store,
    BanList bans,
    AccountSessions sessions,
    ItemData items,
    FormulaData formulas,
    CombatStats combat,
    Experience experience,
    MapData maps,
    ILogger<GmCommands> logger)
{
    /// <summary>The sub code of the GM status packet <c>16/200</c>.</summary>
    public const byte StatusSub = 200;

    /// <summary>The lock time in seconds (9999 days) sent with the locked message to a banned account that tries to log in.</summary>
    public const int BanSeconds = 9999 * 86400;

    /// <summary>The command names, compared without case.</summary>
    public static readonly IReadOnlySet<string> Names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "op", "deop", "kick", "ban", "ban-ip", "unban", "gamemode", "give", "level", "tp", "tphere", "gm", "help",
    };

    /// <summary>The lines <c>/help</c> shows a GM, one per command.</summary>
    private static readonly string[] HelpLines =
        [
            "/help: this list",
            "/op <name>: make an account a GM",
            "/deop <name>: take GM away from an account",
            "/kick <name>: disconnect an online player",
            "/ban <name>: ban an account",
            "/ban-ip <name>: ban an account and its address",
            "/unban <name>: lift the bans of a name or address",
            "/gamemode admin|normal: switch admin mode",
            "/give <item> [count]: put an item in your bag",
            "/level <n> [name]: set a level",
            "/tp <map> <x> <y> | /tp <name> [map x y] | /tp <a> <b>: teleport",
            "/tphere <name>: bring a player to you",
        ];

    /// <summary>The help line listing all commands, used as the reply when a command name has no handler.</summary>
    private const string Help =
        "/op /deop /kick /ban /ban-ip /unban <name>; /gamemode admin|normal; /give <item> [count]; /level <n> [name]; " +
            "/tp <map> <x> <y> | /tp <name> [map x y] | /tp <a> <b>; /tphere <name>";

    /// <summary>The packet <c>16/200</c> telling the client whether the player is a GM and whether admin mode is on.</summary>
    /// <param name="gm">True when the account is a GM.</param>
    /// <param name="adminMode">True when admin mode is on.</param>
    /// <returns>The packet; bit 1 is the GM flag, bit 2 the admin mode.</returns>
    public static byte[] Status(bool gm, bool adminMode) =>
        new PacketWriter(16, StatusSub).U8((byte)((gm ? 1 : 0) | (adminMode ? 2 : 0))).ToArray();

    /// <summary>The packet <c>2/12</c> showing a system line in the chat.</summary>
    /// <param name="text">The line.</param>
    /// <returns>The packet.</returns>
    public static byte[] SystemLine(string text) => new PacketWriter(Action2.Code, 12).RawString(text).ToArray();

    /// <summary>Sends the GM status to a player who has just entered the world, when the account is a GM.</summary>
    /// <param name="session">The player's session.</param>
    public static void SendOnLogin(GameSession session)
    {
        if (session.Account is { Gm: true })
            session.Send(Status(true, session.AdminMode));
    }

    /// <summary>
    /// Runs a chat line as a GM command when it starts with <c>/</c> and names one; a non-GM is refused and the reply goes back as a system line.
    /// </summary>
    /// <param name="session">The sender's session.</param>
    /// <param name="text">The chat text.</param>
    /// <returns>True when the line was a GM command name (it must not be chatted), false when it is ordinary chat.</returns>
    public async ValueTask<bool> TryHandleAsync(GameSession session, string text)
    {
        if (text.Length < 2 || text[0] != '/')
            return false;
        var words = text[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0 || !Names.Contains(words[0]))
            return false;
        var gm = session.Account!;
        var command = words[0].ToLowerInvariant();
        if (command == "help")
        {
            foreach (var line in gm.Gm ? HelpLines : ["The slash commands are for GMs."])
                Reply(session, line);
            return true;
        }
        if (!gm.Gm)
        {
            logger.LogWarning("[{Session}] {Name} is not a GM: /{Command} refused", session.Id, session.ActiveCharacter!.Name, command);
            Reply(session, $"Only a GM can use /{command}.");
            return true;
        }
        logger.LogInformation("[{Session}] GM {Name}: {Text}", session.Id, session.ActiveCharacter!.Name, text);
        var args = words[1..];
        var reply = command switch
        {
            "op" => await SetGmAsync(session, args, on: true).ConfigureAwait(false), // /op: makes an account a GM
            "deop" => await SetGmAsync(session, args, on: false).ConfigureAwait(false), // /deop: takes GM away from an account
            "kick" => Kick(session, args), // /kick: disconnects an online player
            "ban" => Ban(session, args, address: false), // /ban: bans the account
            "ban-ip" => Ban(session, args, address: true), // /ban-ip: bans the account and the address
            "unban" => Unban(args), // /unban: lifts the bans of a name or address
            "gamemode" => GameMode(session, args), // /gamemode: switches admin mode
            "give" => Give(session, args), // /give: puts an item in the GM's bag
            "level" => Level(session, args), // /level: sets a level
            "tp" => Teleport(session, args), // /tp: moves a player to a position or to another player
            "tphere" => args.Length == 1 ? Move(Online(args[0]), session) : "Usage: /tphere <name>", // /tphere: moves a named player to the GM
            _ => Help, // unknown command: the help line
        };
        Reply(session, reply);
        return true;
    }

    /// <summary>Sends a system line to a player.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="text">The line.</param>
    private static void Reply(GameSession session, string text) => session.Send(SystemLine(text));

    /// <summary>Finds an online player by character name, else by account name.</summary>
    /// <param name="name">The name, compared without case.</param>
    /// <returns>The player's session, or null when nobody online has the name.</returns>
    private GameSession? Online(string name) => world.OnlineSessions().FirstOrDefault(s =>
        string.Equals(s.ActiveCharacter?.Name, name, StringComparison.OrdinalIgnoreCase)) ?? world.OnlineSessions().FirstOrDefault(s =>
            s.ActiveCharacter is not null && string.Equals(s.Account?.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Finds an account by one of its character names, else by account name.</summary>
    /// <param name="name">The name, compared without case.</param>
    /// <returns>The account, or null.</returns>
    private Account? FindAccount(string name)
    {
        var all = store.All();
        return all.FirstOrDefault(a => a.Characters.Exists(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
            ?? all.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Handles <c>/op</c> and <c>/deop</c>: makes an account a GM or takes it away, saves it and tells the player if online.</summary>
    /// <param name="session">The GM's session.</param>
    /// <param name="args">The command arguments: the name.</param>
    /// <param name="on">True for <c>/op</c>, false for <c>/deop</c>.</param>
    /// <returns>The reply line.</returns>
    private async ValueTask<string> SetGmAsync(GameSession session, string[] args, bool on)
    {
        if (args.Length != 1)
            return on ? "Usage: /op <name>" : "Usage: /deop <name>";
        if (FindAccount(args[0]) is not { } account)
            return $"No player or account named {args[0]}.";
        if (!on && ReferenceEquals(account, session.Account))
            return "You cannot /deop yourself.";
        if (account.Gm == on)
            return $"{args[0]} {(on ? "is already" : "is not")} a GM.";
        account.Gm = on;
        await store.SaveAsync(account).ConfigureAwait(false);
        if (world.SessionOf(account.LoginId) is { } target && target.ActiveCharacter is not null)
        {
            if (!on)
                target.AdminMode = false;
            target.Send(Status(on, target.AdminMode));
            Reply(target, on ? "You are now a GM." : "You are no longer a GM.");
        }
        logger.LogInformation("GM {By}: account {Account} {Change}", session.ActiveCharacter!.Name, account.Name, on ? "made a GM" : "no longer a GM");
        return $"{args[0]} {(on ? "is now" : "is no longer")} a GM.";
    }

    /// <summary>Handles <c>/kick</c>: disconnects an online player.</summary>
    /// <param name="session">The GM's session.</param>
    /// <param name="args">The command arguments: the name.</param>
    /// <returns>The reply line.</returns>
    private string Kick(GameSession session, string[] args)
    {
        if (args.Length != 1)
            return "Usage: /kick <name>";
        if (Online(args[0]) is not { } target)
            return $"{args[0]} is not online.";
        Disconnect(session, target);
        return $"{args[0]} was kicked.";
    }

    /// <summary>
    /// Tells a player's client it is disconnected, closes the connection and takes the player out of the world (<see cref="WorldState.Disconnect"/>).
    /// </summary>
    /// <param name="session">The GM's session, whose gate the command holds.</param>
    /// <param name="target">The player's session.</param>
    private void Disconnect(GameSession session, GameSession target)
    {
        logger.LogInformation("[{Session}] {Name} is kicked by a GM", target.Id, target.ActiveCharacter?.Name);
        target.Send(ServerMessages.Kick(KickReason.Disconnected));
        world.Disconnect(session, target);
    }

    /// <summary>
    /// Handles <c>/ban</c> and <c>/ban-ip</c>: bans the account, and for an IP ban also the address of the connected player, then disconnects
    /// every session of the account: the one in the world and the one at character select.
    /// </summary>
    /// <param name="session">The GM's session.</param>
    /// <param name="args">The command arguments: the name.</param>
    /// <param name="address">True for <c>/ban-ip</c>.</param>
    /// <returns>The reply line.</returns>
    private string Ban(GameSession session, string[] args, bool address)
    {
        if (args.Length != 1)
            return address ? "Usage: /ban-ip <name>" : "Usage: /ban <name>";
        if (FindAccount(args[0]) is not { } account)
            return $"No player or account named {args[0]}.";
        if (ReferenceEquals(account, session.Account))
            return "You cannot ban yourself.";
        GameSession[] connected =
            [.. new[] { world.SessionOf(account.LoginId), sessions.SessionOf(account.LoginId) }.OfType<GameSession>().Distinct()];
        IPAddress? ip = null;
        if (address)
        {
            ip = connected.Select(s => s.RemoteEndPoint?.Address).FirstOrDefault(a => a is not null);
            if (ip is null)
                return $"{args[0]} must be connected for an IP ban (the server keeps no addresses).";
            if (IPAddress.IsLoopback(ip) || BanList.Normal(ip) == (session.RemoteEndPoint is { } own ? BanList.Normal(own.Address) : null))
                return $"{args[0]} plays from this PC or your own address ({BanList.Normal(ip)}); not banned, as it would lock you out too.";
        }
        var by = session.ActiveCharacter!.Name;
        var added = bans.BanAccount(account.Name, by);
        if (ip is not null)
            added |= bans.BanAddress(ip, account.Name, by);
        foreach (var target in connected)
            Disconnect(session, target);
        return !added ? $"{args[0]} was already banned."
            : ip is null ? $"{args[0]} (account {account.Name}) is banned."
                : $"{args[0]} (account {account.Name}, {BanList.Normal(ip)}) is banned.";
    }

    /// <summary>Handles <c>/unban</c>: lifts the bans of an account or address.</summary>
    /// <param name="args">The command arguments: the name or address.</param>
    /// <returns>The reply line.</returns>
    private string Unban(string[] args)
    {
        if (args.Length != 1)
            return "Usage: /unban <name or address>";
        var name = FindAccount(args[0])?.Name ?? args[0];
        var lifted = bans.Unban(name);
        return lifted == 0 ? $"{args[0]} is not banned." : $"{args[0]}: {lifted} ban(s) lifted.";
    }

    /// <summary>Handles <c>/gamemode</c>: switches the GM's admin mode on or off and sends the new status.</summary>
    /// <param name="session">The GM's session.</param>
    /// <param name="args">The command arguments: <c>admin</c> or <c>normal</c>.</param>
    /// <returns>The reply line.</returns>
    private static string GameMode(GameSession session, string[] args)
    {
        bool? admin = args.Length == 1 ? args[0].ToLowerInvariant() switch
        {
            "admin" or "creative" or "1" => true, // admin, creative or 1: admin mode on
            "normal" or "survival" or "0" => false, // normal, survival or 0: admin mode off
            _ => null, // any other word: usage line
        } : null;
        if (admin is not { } on)
            return "Usage: /gamemode admin|normal";
        session.AdminMode = on;
        session.Send(Status(true, on));
        return on ? "Admin mode: you take no damage, and the admin item browser is open." : "Normal mode.";
    }

    /// <summary>Handles <c>/give</c>: puts an item in the GM's bag.</summary>
    /// <param name="session">The GM's session.</param>
    /// <param name="args">The command arguments: the item id and an optional count.</param>
    /// <returns>The reply line.</returns>
    private string Give(GameSession session, string[] args)
    {
        if (args.Length is < 1 or > 2 || !ushort.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out var itemId))
            return "Usage: /give <item id> [count]";
        var count = 1;
        if (args.Length == 2 && (!int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out count) || count is < 1 or > 999))
            return "The count is 1 to 999.";
        if (items[itemId] is not { } item)
            return $"Item {itemId} is not in Item.dat.";
        var character = session.ActiveCharacter!;
        var changes = Bag.Add(character, items, itemId, count);
        foreach (var (stack, added) in changes)
            session.Send(ItemMessages.BagAdd(new BagEntry { Slot = stack.Slot, ItemId = itemId, Count = added, Wear = stack.Wear }));
        var given = changes.Sum(c => c.Added);
        logger.LogInformation("[{Session}] GM {Name} gives himself {Count} x {Item} ({Id})", session.Id, character.Name, given, item.Name.Trim(), itemId);
        return given == 0
            ? "Your bag is full."
            : given < count ? $"{given} x {item.Name.Trim()} given; your bag is full." : $"{given} x {item.Name.Trim()} given.";
    }

    /// <summary>
    /// Handles <c>/level</c>: sets the level of the GM or of a named online player, under that player's gate (<see
    /// cref="GameSession.TryExclusiveFrom"/>, as the GM's packet holds the GM's gate).
    /// </summary>
    /// <param name="session">The GM's session.</param>
    /// <param name="args">The command arguments: the level and an optional name.</param>
    /// <returns>The reply line.</returns>
    private string Level(GameSession session, string[] args)
    {
        if (args.Length is < 1 or > 2 || !int.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out var level))
            return "Usage: /level <level> [name]";
        var target = args.Length == 2 ? Online(args[1]) : session;
        if (target is null)
            return $"{args[1]} is not online.";
        if (!target.TryExclusiveFrom(session, out var held))
        {
            logger.LogWarning("[{Session}] GM /level of {Target}: refused, the player is busy or leaving", session.Id, args[1]);
            return $"{args[1]} is busy or leaving; try again.";
        }
        using (held)
            return LevelHeld(target, level);
    }

    /// <summary>Sets the level of a player whose gate the caller holds, after checking it can be set.</summary>
    /// <param name="target">The player's session.</param>
    /// <param name="level">The new level.</param>
    /// <returns>The reply line.</returns>
    private string LevelHeld(GameSession target, int level)
    {
        if (target.ActiveCharacter is not { } character)
            return "The player is not online.";
        if (formulas.Table is null)
            return "Formula.dat is not loaded.";
        var max = FormulaTable.MaxLevel(character.RebornCount);
        if (level < 1 || level > max)
            return $"The level is 1 to {max}.";
        if (target.Battle is not null)
            return $"{character.Name} is in a battle.";
        SetLevel(target, level);
        return $"{character.Name} is level {character.Level}.";
    }

    /// <summary>Sets a character's level by setting its experience to the level's total, with the level-up or the stat recalculation that follows.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="level">The new level.</param>
    internal void SetLevel(GameSession session, int level)
    {
        var character = session.ActiveCharacter!;
        var before = character.Level;
        character.Exp = formulas.Table!.TotalLevelExp(level, character.RebornCount);
        session.Send(InnMessages.SetAttribute(Action8.AttrExp, character.Exp));
        if (level > before)
        {
            experience.ApplyLevelUps(session, announceToSelf: true);
            return;
        }
        character.Level = (byte)level;
        combat.Recalculate(character);
        character.Hp = Math.Min(character.Hp, character.MaxHp);
        character.Sp = Math.Min(character.Sp, character.MaxSp);
        session.Send(InnMessages.SetAttribute(Action8.AttrLevel, character.Level));
        session.Send(InnMessages.SetAttribute(Action8.AttrMaxHp, character.MaxHp));
        session.Send(InnMessages.SetAttribute(Action8.AttrMaxSp, character.MaxSp));
        session.Send(InnMessages.SetAttribute(Action8.AttrHp, character.Hp));
        session.Send(InnMessages.SetAttribute(Action8.AttrSp, character.Sp));
        combat.Send(session);
        world.Broadcast(session, PlayerMessages.SceneLevel(session.Account!.LoginId, (byte)Math.Min(byte.MaxValue, character.Level)), sameMapOnly: true);
        logger.LogInformation("[{Session}] {Name} set to level {Level} (was {Before})", session.Id, character.Name, character.Level, before);
    }

    /// <summary>Handles <c>/tp</c>: moves the GM, or a named player, to a map position or to another player.</summary>
    /// <param name="session">The GM's session.</param>
    /// <param name="args">The command arguments, in one of the forms of the usage line.</param>
    /// <returns>The reply line.</returns>
    private string Teleport(GameSession session, string[] args)
    {
        static ushort? Number(string s) => ushort.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
        switch (args.Length)
        {
            case 3 when Number(args[0]) is { } map && Number(args[1]) is { } x && Number(args[2]) is { } y: // /tp <map> <x> <y>: moves the GM to the position
                return Warp(session, map, x, y);
            case 4 when Number(args[1]) is { } map && Number(args[2]) is { } x && Number(args[3]) is { } y: // /tp <name> <map> <x> <y>: moves that player
                return Online(args[0]) is { } who ? Warp(who, map, x, y) : $"{args[0]} is not online.";
            case 1: // /tp <name>: moves the GM to that player
                return Move(session, Online(args[0]));
            case 2: // /tp <name> <to name>: moves the first player to the second
                return Online(args[0]) is { } mover ? Move(mover, Online(args[1])) : $"{args[0]} is not online.";
            default: // any other form: usage line
                return "Usage: /tp <map> <x> <y> | /tp <name> <map> <x> <y> | /tp <name> | /tp <name> <to name>";
        }
    }

    /// <summary>Moves one player to the place of another.</summary>
    /// <param name="mover">The player to move.</param>
    /// <param name="to">The player to move to.</param>
    /// <returns>The reply line.</returns>
    private string Move(GameSession? mover, GameSession? to)
    {
        if (mover?.ActiveCharacter is null)
            return "That player is not online.";
        if (to?.ActiveCharacter is not { } there)
            return "That player is not online.";
        if (ReferenceEquals(mover, to))
            return "Nobody to move.";
        return Warp(mover, there.MapId, there.X, there.Y);
    }

    /// <summary>Changes a player's map and position after checking that the player is not in a battle and the position is on the map.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="map">The target map id.</param>
    /// <param name="x">The target x tile.</param>
    /// <param name="y">The target y tile.</param>
    /// <returns>The reply line.</returns>
    private string Warp(GameSession session, ushort map, ushort x, ushort y)
    {
        var character = session.ActiveCharacter!;
        if (session.Battle is not null)
            return $"{character.Name} is in a battle.";
        var bounds = maps.MapSize(map);
        if (maps.Available && bounds is null && maps.SceneName(map).Length == 0)
            return $"Map {map} does not exist.";
        if (bounds is { } b && (x >= b.Width || y >= b.Height))
            return $"({x}, {y}) is outside map {map} ({b.Width} x {b.Height}).";
        session.Event = null;
        world.ChangeMap(session, new DoorWarp(map, x, y, 0));
        return $"{character.Name} -> map {map} ({x}, {y}).";
    }
}
