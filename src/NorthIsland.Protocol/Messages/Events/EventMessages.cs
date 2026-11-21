using NorthIsland.Protocol.IO;
using NorthIsland.Protocol.Opcodes;

namespace NorthIsland.Protocol.Messages.Events;

/// <summary>Builders for the event packets: event results (action 20), map NPC changes (action 22) and sport game packets (action 57).</summary>
public static class EventMessages
{
    /// <summary>Sub code of the first event result packet of an event (20/1).</summary>
    public const byte SubFirst = 1;

    /// <summary>Sub code of the event result packets after the first (20/6).</summary>
    public const byte SubNext = 6;

    /// <summary>Builds a 20/x packet that carries one event result.</summary>
    /// <param name="sub">Sub code of the packet.</param>
    /// <param name="r">The result for the client to carry out.</param>
    /// <returns>The packet.</returns>
    public static byte[] Result(byte sub, EventResult r) =>
        new PacketWriter(Action20.Code, sub)
            .U8(0).U16(0).U8(0)
            .U8(r.Kind).U8(r.Mode).U16(r.Target).U8(r.Option).I32(r.Value).U16(r.DataId)
            .U8(0)
            .ToArray();

    /// <summary>
    /// Builds the <c>20/58</c> notice with choice 4, which the client shows as "At present, the function is not available" (its
    /// EventNotice table) whatever window is open.
    /// </summary>
    /// <returns>The packet.</returns>
    public static byte[] FunctionUnavailable() =>
        new PacketWriter(Action20.Code, Action20.BuyLevelFlow).U8(Action20.FunctionUnavailable).ToArray();

    /// <summary>Builds 22/1, which sets the state of a map NPC.</summary>
    /// <param name="role">Index of the map NPC.</param>
    /// <param name="state">The new state.</param>
    /// <returns>The packet.</returns>
    public static byte[] NpcState(ushort role, byte state) => new PacketWriter(Action22.Code, Action22.State).U16(role).U8(state).ToArray();

    /// <summary>Builds 22/3, which hides a map NPC for some time.</summary>
    /// <param name="role">Index of the map NPC.</param>
    /// <param name="seconds">How long it stays hidden.</param>
    /// <returns>The packet.</returns>
    public static byte[] NpcHide(ushort role, int seconds) => new PacketWriter(Action22.Code, Action22.Hide).U16(role).I32(seconds).ToArray();

    /// <summary>Builds 22/2, which makes a map NPC walk to a position.</summary>
    /// <param name="role">Index of the map NPC.</param>
    /// <param name="x">Target x position.</param>
    /// <param name="y">Target y position.</param>
    /// <param name="speed">Walking speed.</param>
    /// <returns>The packet.</returns>
    public static byte[] NpcMove(ushort role, ushort x, ushort y, byte speed) =>
        new PacketWriter(Action22.Code, Action22.Move).U16(role).U16(x).U16(y).U8(speed).ToArray();

    /// <summary>Builds 22/5, which puts a map NPC at a position at once.</summary>
    /// <param name="role">Index of the map NPC.</param>
    /// <param name="x">The x position.</param>
    /// <param name="y">The y position.</param>
    /// <returns>The packet.</returns>
    public static byte[] NpcPlace(ushort role, ushort x, ushort y) =>
        new PacketWriter(Action22.Code, Action22.Place).U16(role).U16(x).U16(y).ToArray();

    /// <summary>Builds 22/12, which sets the minimap sign of an object.</summary>
    /// <param name="kind">Kind of object: 1 NPC, 2 door, 3 mine.</param>
    /// <param name="index">Index of the object.</param>
    /// <param name="sign">Sign number, 0 for none.</param>
    /// <returns>The packet.</returns>
    public static byte[] MapSign(byte kind, ushort index, byte sign) =>
        new PacketWriter(Action22.Code, Action22.Sign).U8(kind).U16(index).U8(sign).ToArray();

    /// <summary>Builds 22/11, which shows a hidden map NPC for some time.</summary>
    /// <param name="role">Index of the map NPC.</param>
    /// <param name="seconds">How long it is shown.</param>
    /// <returns>The packet.</returns>
    public static byte[] NpcShowFor(ushort role, ushort seconds) =>
        new PacketWriter(Action22.Code, Action22.ShowFor).U16(role).U16(seconds).ToArray();

    /// <summary>Builds 22/10, which hides a map NPC for some time.</summary>
    /// <param name="role">Index of the map NPC.</param>
    /// <param name="seconds">How long it stays hidden.</param>
    /// <returns>The packet.</returns>
    public static byte[] NpcHideFor(ushort role, ushort seconds) =>
        new PacketWriter(Action22.Code, Action22.HideFor).U16(role).U16(seconds).ToArray();

    /// <summary>Builds 22/7, which makes a map NPC show an emotion.</summary>
    /// <param name="role">Index of the map NPC.</param>
    /// <param name="emotion">The emotion number.</param>
    /// <returns>The packet.</returns>
    public static byte[] NpcEmotion(ushort role, byte emotion) => new PacketWriter(Action22.Code, Action22.Emotion).U16(role).U8(emotion).ToArray();

    /// <summary>Builds 22/8, which makes a map NPC play an action.</summary>
    /// <param name="role">Index of the map NPC.</param>
    /// <param name="action">The action number.</param>
    /// <returns>The packet.</returns>
    public static byte[] NpcAction(ushort role, byte action) => new PacketWriter(Action22.Code, Action22.Action).U16(role).U8(action).ToArray();

    /// <summary>Builds 22/9, which makes a map NPC play an action facing a direction.</summary>
    /// <param name="role">Index of the map NPC.</param>
    /// <param name="action">The action number.</param>
    /// <param name="direction">The direction.</param>
    /// <returns>The packet.</returns>
    public static byte[] NpcActionDir(ushort role, byte action, byte direction) =>
        new PacketWriter(Action22.Code, Action22.ActionDir).U16(role).U8(action).U8(direction).ToArray();

    /// <summary>Builds 57/1, which starts a sport game on the client.</summary>
    /// <param name="kind">Kind of game.</param>
    /// <param name="value">Game value sent with the kind.</param>
    /// <param name="level">Level of the game.</param>
    /// <returns>The packet.</returns>
    public static byte[] SportStart(byte kind, ushort value, byte level) =>
        new PacketWriter(Action57.Code, Action57.Start).U8(kind).U16(value).U8(level).U8(0).ToArray();

    /// <summary>Builds 57/2, which frees the player from a sport game.</summary>
    /// <returns>The packet.</returns>
    public static byte[] SportFree() => new PacketWriter(Action57.Code, Action57.Free).ToArray();

    /// <summary>Builds 20/12, which tells the client an event is running and the server answers next.</summary>
    /// <returns>The packet.</returns>
    public static byte[] ServerStart() => new PacketWriter(Action20.Code, Action20.ServerStart).ToArray();

    /// <summary>Builds 20/9, which tells the client to stop waiting for the server.</summary>
    /// <returns>The packet.</returns>
    public static byte[] StopWaiting() => new PacketWriter(Action20.Code, Action20.StopWaiting).ToArray();

    /// <summary>Builds 20/8, which tells the client the event is finished.</summary>
    /// <returns>The packet.</returns>
    public static byte[] Finished() => new PacketWriter(Action20.Code, Action20.Finished).ToArray();

    /// <summary>Builds 20/46, a treasure jump for an NPC.</summary>
    /// <param name="npcIndex">Index of the NPC.</param>
    /// <returns>The packet.</returns>
    public static byte[] TreasureJump(ushort npcIndex) => new PacketWriter(Action20.Code, Action20.TreasureJump).U16(npcIndex).ToArray();

    /// <summary>Builds 20/10, which asks the client for the next step of an event.</summary>
    /// <returns>The packet.</returns>
    public static byte[] RequestNext() => new PacketWriter(Action20.Code, 10).ToArray();
}
