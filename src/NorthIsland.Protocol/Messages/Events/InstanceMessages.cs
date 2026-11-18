using NorthIsland.Protocol.IO;
using NorthIsland.Protocol.Opcodes;

namespace NorthIsland.Protocol.Messages.Events;

/// <summary>Builders for the instance packets (action 85).</summary>
public static class InstanceMessages
{
    /// <summary>Starts an 85 packet with the given sub code.</summary>
    /// <param name="sub">The sub code.</param>
    /// <returns>The writer, positioned after the sub code.</returns>
    private static PacketWriter W(byte sub) => new(Action85.Code, sub);

    /// <summary>Builds 85/1, one page of the open instances.</summary>
    /// <param name="pages">Number of pages.</param>
    /// <param name="page">Number of this page.</param>
    /// <param name="rows">The instances on this page.</param>
    /// <returns>The packet.</returns>
    public static byte[] OrderList(byte pages, byte page, IReadOnlyList<InstanceRow> rows)
    {
        var w = W(Action85.OrderList).U8(pages).U8(page).U8((byte)rows.Count);
        foreach (var r in rows)
            w.U16(r.MapId).PStr8(r.Creator).PStr8(Clip(r.Message)).U8(r.Players).PStr8(r.Guild);
        return w.ToArray();
    }

    /// <summary>Builds 85/8, which adds one instance to the list.</summary>
    /// <param name="pages">Number of pages.</param>
    /// <param name="r">The new instance.</param>
    /// <returns>The packet.</returns>
    public static byte[] OrderRow(byte pages, InstanceRow r) =>
        W(Action85.OrderRow).U8(pages).U16(r.MapId).PStr8(r.Creator).PStr8(Clip(r.Message)).U8(r.Players).PStr8(r.Guild).ToArray();

    /// <summary>Builds 85/2, the answer to creating an instance or a notice.</summary>
    /// <param name="code">One of <see cref="InstanceInitCodes"/>.</param>
    /// <returns>The packet.</returns>
    public static byte[] InitResult(byte code) => W(Action85.InitResult).U8(code).ToArray();

    /// <summary>Builds 85/3, the answer to joining an instance.</summary>
    /// <param name="code">One of <see cref="InstanceJoinCodes"/>.</param>
    /// <returns>The packet.</returns>
    public static byte[] JoinResult(byte code) => W(Action85.JoinResult).U8(code).ToArray();

    /// <summary>Builds 85/4, the detail window of one instance.</summary>
    /// <param name="mapId">Map id of the instance.</param>
    /// <param name="members">Name and id of each member.</param>
    /// <returns>The packet.</returns>
    public static byte[] Detail(ushort mapId, IReadOnlyList<(string Name, int Id)> members)
    {
        var w = W(Action85.DetailInfo).U16(mapId).U8((byte)members.Count);
        foreach (var (name, id) in members)
            w.PStr8(name).I32(id);
        return w.ToArray();
    }

    /// <summary>Builds 85/5/1, which tells a player which instance they belong to.</summary>
    /// <param name="state">1 waiting, 2 started.</param>
    /// <param name="mapId">Map id of the instance.</param>
    /// <param name="members">Number of members.</param>
    /// <param name="message">The creator's message.</param>
    /// <param name="creatorId">Player id of the creator.</param>
    /// <returns>The packet.</returns>
    public static byte[] State(
        byte state,
        ushort mapId,
        byte members,
        string message,
        int creatorId) =>
            W(Action85.State).U8(1).U8(state).U16(mapId).U8(members).PStr8(Clip(message)).I32(creatorId).ToArray();

    /// <summary>Builds 85/5/2, which tells the player they are no longer in an instance.</summary>
    /// <returns>The packet.</returns>
    public static byte[] Quit() => W(Action85.State).U8(2).ToArray();

    /// <summary>Builds 85/5/3, which tells the player the instance has started.</summary>
    /// <returns>The packet.</returns>
    public static byte[] Started() => W(Action85.State).U8(3).ToArray();

    /// <summary>Builds 85/5/4, which tells the player the instance was refused.</summary>
    /// <returns>The packet.</returns>
    public static byte[] Refused() => W(Action85.State).U8(4).ToArray();

    /// <summary>Builds 85/6, one page of member ids.</summary>
    /// <param name="pages">Number of pages.</param>
    /// <param name="page">Number of this page.</param>
    /// <param name="members">Total number of members.</param>
    /// <param name="ids">The member ids on this page.</param>
    /// <returns>The packet.</returns>
    public static byte[] MemberList(byte pages, byte page, byte members, IReadOnlyList<int> ids)
    {
        var w = W(Action85.MemberList).U8(pages).U8(page).U8(members).U8((byte)ids.Count);
        foreach (var id in ids)
            w.I32(id);
        return w.ToArray();
    }

    /// <summary>Builds 85/7, which tells that a member joined.</summary>
    /// <param name="pages">Number of pages.</param>
    /// <param name="members">Number of members now.</param>
    /// <param name="id">Player id of the new member.</param>
    /// <param name="mapId">Map id of the instance.</param>
    /// <returns>The packet.</returns>
    public static byte[] MemberJoined(byte pages, byte members, int id, ushort mapId) =>
        W(Action85.MemberJoined).U8(pages).U8(members).I32(id).U16(mapId).ToArray();

    /// <summary>Builds 85/9/1, the time left. The client stores the value as minutes and counts it down one a minute (RecvDupRemainTime).</summary>
    /// <param name="minutes">Minutes left.</param>
    /// <returns>The packet.</returns>
    public static byte[] RemainTime(int minutes) => W(Action85.RemainTime).U8(1).I32(minutes).ToArray();

    /// <summary>Builds 85/10/1, the progress of one goal.</summary>
    /// <param name="slot">Goal slot, 1 to 11.</param>
    /// <param name="count">How many have been done now.</param>
    /// <param name="by">Name of the player who did the last one.</param>
    /// <returns>The packet.</returns>
    public static byte[] GoalCount(byte slot, byte count, string by) => W(Action85.Goal).U8(1).U8(slot).U8(count).PStr8(by).ToArray();

    /// <summary>Builds 85/10/2, which reports the boss as done.</summary>
    /// <param name="done">Done flag byte.</param>
    /// <param name="by">Name of the player who did it.</param>
    /// <returns>The packet.</returns>
    public static byte[] BossDone(byte done, string by) => W(Action85.Goal).U8(2).U8(done).PStr8(by).ToArray();

    /// <summary>Builds 85/10/3, the definition of one goal.</summary>
    /// <param name="slot">Goal slot, 1 to 11.</param>
    /// <param name="needed">How many are needed.</param>
    /// <param name="npcId">Id of the NPC to beat.</param>
    /// <returns>The packet.</returns>
    public static byte[] GoalDefined(byte slot, byte needed, ushort npcId) => W(Action85.Goal).U8(3).U8(slot).U8(needed).U16(npcId).ToArray();

    /// <summary>Builds 85/10/4, the definition of the boss goal.</summary>
    /// <param name="npcId">Id of the boss NPC.</param>
    /// <returns>The packet.</returns>
    public static byte[] BossDefined(ushort npcId) => W(Action85.Goal).U8(4).U16(npcId).ToArray();

    /// <summary>Builds 85/11, which removes an instance from the list.</summary>
    /// <param name="reason">1 started, 2 cancelled.</param>
    /// <param name="mapId">Map id of the instance.</param>
    /// <returns>The packet.</returns>
    public static byte[] OrderClosed(byte reason, ushort mapId) => W(Action85.OrderClosed).U8(reason).U16(mapId).ToArray();

    /// <summary>Builds 85/12, which tells that a member left.</summary>
    /// <param name="members">Number of members now.</param>
    /// <param name="id">Player id of the member who left.</param>
    /// <param name="mapId">Map id of the instance.</param>
    /// <param name="creator">True when the member was the creator.</param>
    /// <returns>The packet.</returns>
    public static byte[] MemberLeft(byte members, int id, ushort mapId, bool creator) =>
        W(Action85.MemberLeft).U8(members).I32(id).U16(mapId).U8(creator ? (byte)1 : (byte)0).ToArray();

    /// <summary>Cuts a text to the 28 characters a message may have.</summary>
    /// <param name="s">The text.</param>
    /// <returns>The text, shortened when needed.</returns>
    private static string Clip(string s) => s.Length > 28 ? s[..28] : s;
}
