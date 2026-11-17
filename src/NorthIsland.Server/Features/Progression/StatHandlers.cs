using Microsoft.Extensions.Logging;
using NorthIsland.Protocol.IO;
using NorthIsland.Protocol.Messages.Player;
using NorthIsland.Protocol.Opcodes;

namespace NorthIsland.Server.Features.Progression;

/// <summary>Handles spending of stat points from the status window.</summary>
/// <param name="combat">Recalculates and sends combat stats.</param>
/// <param name="logger">Logger.</param>
public sealed class StatHandlers(
    CombatStats combat,
    ILogger<StatHandlers> logger)
{
    /// <summary>
    /// Handles a stat point spend request: raises the listed attributes if the points are available and no attribute passes the cap of
    /// <see cref="ItemEffectHandlers.AbilityCap"/>, else resends the current values.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: a leading byte, a count, then count pairs of attribute id (byte) and points (int).</param>
    internal ValueTask OnSpend(GameSession session, ReadOnlyMemory<byte> body)
    {
        var character = session.ActiveCharacter;
        if (session.State != SessionState.InWorld || character is null || body.Length < 2 || session.Battle is not null)
            return ValueTask.CompletedTask;

        var r = new PacketReader(body.Span);
        r.U8();
        var count = r.U8();
        var raises = new List<(byte Attr, int Points)>();
        for (var i = 0; i < count && r.Remaining >= 5; i++)
            raises.Add((r.U8(), r.I32()));

        var total = raises.Sum(x => (long)x.Points);
        var cap = ItemEffectHandlers.AbilityCap(character.Potential);
        if (raises.Count == 0 || raises.Any(x => x.Points <= 0 || Stat(character, x.Attr) is null) || total > character.StatPoints
            || raises.GroupBy(x => x.Attr).Any(g => Stat(character, g.Key)!.Value + g.Sum(x => (long)x.Points) > cap))
        {
            logger.LogWarning("[{Session}] {Name}: refused stat points {Raises} (has {Points}, cap {Cap})", session.Id, character.Name,
                string.Join(", ", raises.Select(x => $"{x.Attr}+{x.Points}")), character.StatPoints, cap);
            SendStats(session, character);
            return ValueTask.CompletedTask;
        }

        foreach (var (attr, points) in raises)
            SetStat(character, attr, (ushort)Math.Min(ushort.MaxValue, Stat(character, attr)!.Value + points));
        character.StatPoints -= (ushort)total;
        combat.Recalculate(character);
        SendStats(session, character);
        combat.Send(session);
        logger.LogInformation("[{Session}] {Name} spends {Total} stat points: STR {Str} CON {Con} INT {Int} WIS {Wis} AGI {Agi} ({Left} left)",
            session.Id, character.Name, total, character.Strength, character.Constitution, character.Intelligence, character.Wisdom,
            character.Agility, character.StatPoints);
        return ValueTask.CompletedTask;
    }

    /// <summary>Sends the five stats, the unspent stat points and the HP and SP values to the player.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="c">The player's character.</param>
    private static void SendStats(GameSession session, Character c)
    {
        foreach (var (attr, value) in new (byte, int)[]
        {
            (Action8.AttrStr, c.Strength), (Action8.AttrCon, c.Constitution), (Action8.AttrInt, c.Intelligence),
            (Action8.AttrWis, c.Wisdom), (Action8.AttrAgi, c.Agility), (Action8.AttrStatPoints, c.StatPoints),
            (Action8.AttrMaxHp, c.MaxHp), (Action8.AttrMaxSp, c.MaxSp), (Action8.AttrHp, c.Hp), (Action8.AttrSp, c.Sp),
        })
            session.Send(InnMessages.SetAttribute(attr, value));
    }

    /// <summary>Reads one of the five stats.</summary>
    /// <param name="c">The character.</param>
    /// <param name="attr">The attribute id.</param>
    /// <returns>The value, or null when the id is not one of the five stats.</returns>
    private static ushort? Stat(Character c, byte attr) => attr switch
    {
        Action8.AttrStr => c.Strength, // strength
        Action8.AttrCon => c.Constitution, // constitution
        Action8.AttrInt => c.Intelligence, // intelligence
        Action8.AttrWis => c.Wisdom, // wisdom
        Action8.AttrAgi => c.Agility, // agility
        _ => null, // not one of the five stats: no value
    };

    /// <summary>Writes one of the five stats; other attribute ids are ignored.</summary>
    /// <param name="c">The character.</param>
    /// <param name="attr">The attribute id.</param>
    /// <param name="value">The new value.</param>
    private static void SetStat(Character c, byte attr, ushort value)
    {
        switch (attr)
        {
            case Action8.AttrStr: c.Strength = value; break; // strength
            case Action8.AttrCon: c.Constitution = value; break; // constitution
            case Action8.AttrInt: c.Intelligence = value; break; // intelligence
            case Action8.AttrWis: c.Wisdom = value; break; // wisdom
            case Action8.AttrAgi: c.Agility = value; break; // agility
        }
    }
}
