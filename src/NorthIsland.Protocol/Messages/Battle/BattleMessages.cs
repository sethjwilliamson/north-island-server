using NorthIsland.Protocol.IO;
using NorthIsland.Protocol.Opcodes;

namespace NorthIsland.Protocol.Messages.Battle;

/// <summary>Builders for the packets of a running battle (actions 11, 50, 51, 52 and 53).</summary>
public static class BattleMessages
{
    /// <summary>Attribute id of HP in <see cref="FighterValue"/> and 51/1 packets.</summary>
    public const byte AttrHp = 25;

    /// <summary>Attribute id of SP in <see cref="FighterValue"/> and 51/1 packets.</summary>
    public const byte AttrSp = 26;

    /// <summary>Builds a 50/1 packet with one attacker record.</summary>
    /// <param name="row">Battle row of the attacker.</param>
    /// <param name="col">Battle column of the attacker.</param>
    /// <param name="skillId">Id of the skill or attack used.</param>
    /// <param name="targets">What the action does to each target.</param>
    /// <param name="client">Client generation, which decides how wide the HP amounts are.</param>
    /// <returns>The packet.</returns>
    public static byte[] Action(
        byte row,
        byte col,
        ushort skillId,
        IReadOnlyList<ActionTarget> targets,
        ClientGeneration client) =>
            Actions([new ActionRecord(row, col, skillId, targets)], client);

    /// <summary>Builds a 50/1 packet with several attacker records, one after the other.</summary>
    /// <param name="records">The attacker records, in order.</param>
    /// <param name="client">Client generation, which decides how wide the HP amounts are.</param>
    /// <returns>The packet.</returns>
    public static byte[] Actions(IReadOnlyList<ActionRecord> records, ClientGeneration client)
    {
        var packet = new PacketWriter(Action50.Code, Action50.Skill);
        foreach (var r in records)
        {
            var body = new PacketWriter().U8(r.Row).U8(r.Col).U16(r.SkillId).U8(0).U8((byte)r.Targets.Count);
            foreach (var t in r.Targets)
            {
                body.U8(t.Row).U8(t.Col).U8(t.Hit ? (byte)1 : (byte)0).U8(t.Guard).U8((byte)t.Values.Count);
                foreach (var v in t.Values)
                    body.U8(v.AttrId).Hp(v.Amount, client).U8(!v.Negative ? (byte)0 : v.Critical && client == ClientGeneration.Client2014 ? (byte)2 : (byte)1);
            }
            var record = body.ToArray();
            packet.U16((ushort)record.Length).Bytes(record);
        }
        return packet.ToArray();
    }

    /// <summary>Builds a 50/6 packet that sets which cells around its target a fighter's next action reaches.</summary>
    /// <param name="row">Battle row of the fighter.</param>
    /// <param name="col">Battle column of the fighter.</param>
    /// <param name="area">The client's area code, the Skill.dat area code less one; 0 is the target alone.</param>
    /// <returns>The packet.</returns>
    /// <remarks>The client shows an action's values only on the fighters of this area, so it must come before the 50/1 action.</remarks>
    public static byte[] AttackArea(byte row, byte col, byte area) =>
        new PacketWriter(Action50.Code, Action50.AttackArea).U8(row).U8(col).U8(area).ToArray();

    /// <summary>Builds the packet that puts a status on the fighter in a cell.</summary>
    /// <param name="row">Battle row of the fighter.</param>
    /// <param name="col">Battle column of the fighter.</param>
    /// <param name="kind">Status kind byte.</param>
    /// <param name="skillId">Id of the skill that caused the status.</param>
    /// <param name="subIndex">Sub index within the status kind.</param>
    /// <returns>The packet.</returns>
    public static byte[] Status(
        byte row,
        byte col,
        byte kind,
        ushort skillId,
        byte subIndex = 0) =>
            new PacketWriter(Action53.Code, Action53.Status).U8(row).U8(col).U8(kind).U16(skillId).U8(subIndex).ToArray();

    /// <summary>Builds a 51/1 packet that sets absolute attribute values of fighters.</summary>
    /// <param name="values">Row, column, attribute id and new value of each change.</param>
    /// <returns>The packet.</returns>
    public static byte[] FighterAttributes(IEnumerable<(byte Row, byte Col, byte AttrId, int Value)> values)
    {
        var w = new PacketWriter(Action51.Code, Action51.FighterAttributes);
        foreach (var (row, col, attr, value) in values)
            w.U8(row).U8(col).U8(attr).I32(value);
        return w.ToArray();
    }

    /// <summary>Builds the packet that tells the client the fighter in a cell was knocked off.</summary>
    /// <param name="row">Battle row of the fighter.</param>
    /// <param name="col">Battle column of the fighter.</param>
    /// <returns>The packet.</returns>
    public static byte[] KnockedOff(byte row, byte col) => new PacketWriter(Action53.Code, Action53.KnockedOff).U8(row).U8(col).ToArray();

    /// <summary>Builds a 53/4 packet that sends an item icon flying from one fighter to a list of cells.</summary>
    /// <param name="itemId">Id of the thrown item.</param>
    /// <param name="srcRow">Battle row of the throwing fighter.</param>
    /// <param name="srcCol">Battle column of the throwing fighter.</param>
    /// <param name="targets">Row and column of each target cell.</param>
    /// <returns>The packet.</returns>
    public static byte[] ThrowItem(ushort itemId, byte srcRow, byte srcCol, IEnumerable<(byte Row, byte Col)> targets)
    {
        var w = new PacketWriter(Action53.Code, Action53.ThrowItem).U16(itemId).U8(srcRow).U8(srcCol);
        foreach (var (row, col) in targets)
            w.U8(row).U8(col);
        return w.ToArray();
    }

    /// <summary>Builds the 11/250 packet that starts a battle and lists every fighter.</summary>
    /// <param name="background">Id of the battle background.</param>
    /// <param name="eventBattle">Event battle flag byte.</param>
    /// <param name="fighters">The fighters on the field.</param>
    /// <param name="client">Client generation, which decides the record layout.</param>
    /// <returns>The packet.</returns>
    public static byte[] FightStart(byte background, byte eventBattle, IEnumerable<FighterRecord> fighters, ClientGeneration client)
    {
        var w = new PacketWriter(Action11.Code, Action11.FightStart).U8(background).U8(eventBattle);
        foreach (var f in fighters)
            f.WriteTo(w, client);
        return w.ToArray();
    }

    /// <summary>Builds an 11/5 packet for fighters that enter a running battle.</summary>
    /// <param name="fighters">The joining fighters.</param>
    /// <param name="client">Client generation, which decides the record layout.</param>
    /// <returns>The packet.</returns>
    public static byte[] Join(IEnumerable<FighterRecord> fighters, ClientGeneration client)
    {
        var w = new PacketWriter(Action11.Code, Action11.Join).U8(0);
        foreach (var f in fighters)
            f.WriteTo(w, client, withState: false);
        return w.ToArray();
    }

    /// <summary>Builds an 11/251 packet with the weapon look of fighters.</summary>
    /// <param name="looks">Fighter id, owner id, row, column and weapon item id of each fighter.</param>
    /// <returns>The packet.</returns>
    public static byte[] EquipLook(IEnumerable<(int Id, int Owner, byte Row, byte Col, ushort Item)> looks)
    {
        var w = new PacketWriter(Action11.Code, Action11.EquipLook).U8(0);
        foreach (var l in looks)
            w.I32(l.Id).I32(l.Owner).U8(l.Row).U8(l.Col).U16(l.Item);
        return w.ToArray();
    }

    /// <summary>Builds the 11/10 packet that sets the battle type.</summary>
    /// <param name="type">The battle type byte.</param>
    /// <returns>The packet.</returns>
    public static byte[] BattleType(byte type) => new PacketWriter(Action11.Code, Action11.BattleType).U8(type).ToArray();

    /// <summary>Builds the 52/1 packet that starts the command phase of a round.</summary>
    /// <returns>The packet.</returns>
    public static byte[] CommandPhase() => new PacketWriter(Action52.Code, Action52.CommandPhase).ToArray();

    /// <summary>Builds an 11/0 packet that removes a player from the battle.</summary>
    /// <param name="playerId">Id of the player who leaves.</param>
    /// <returns>The packet.</returns>
    public static byte[] PlayerLeft(int playerId) => new PacketWriter(Action11.Code, Action11.PlayerLeft).I32(playerId).U16(0).ToArray();

    /// <summary>Builds an 11/1 packet that removes the fighter in a cell from the battle.</summary>
    /// <param name="row">Battle row of the fighter.</param>
    /// <param name="col">Battle column of the fighter.</param>
    /// <returns>The packet.</returns>
    public static byte[] FighterLeave(byte row, byte col) => new PacketWriter(Action11.Code, Action11.FighterLeave).U8(row).U8(col).ToArray();
}
