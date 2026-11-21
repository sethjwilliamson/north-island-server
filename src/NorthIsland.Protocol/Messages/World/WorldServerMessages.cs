using NorthIsland.Protocol.IO;
using NorthIsland.Protocol.Opcodes;

namespace NorthIsland.Protocol.Messages.World;

/// <summary>Builders for the scene packets that tell clients about other players.</summary>
public static class WorldServerMessages
{
    /// <summary>Builds the packet that shows another player (action 3 with the enter effect, action 4 without).</summary>
    /// <param name="record">The player record.</param>
    /// <param name="arriving">True for action 3, false for action 4.</param>
    /// <returns>The packet.</returns>
    public static byte[] OtherPlayer(OtherPlayerRecord record, bool arriving)
    {
        var w = new PacketWriter(arriving ? Action3.Code : Action4.Code);
        record.WriteTo(w);
        return w.ToArray();
    }

    /// <summary>Builds the <c>12</c> packet that puts a player on a map.</summary>
    /// <param name="playerId">Id of the player.</param>
    /// <param name="mapId">Target map id.</param>
    /// <param name="x">Target x.</param>
    /// <param name="y">Target y.</param>
    /// <param name="conveyIndex">Index of the conveyance used.</param>
    /// <param name="houseRoom">House room number.</param>
    /// <param name="roomSize">Room size 1 to 3; when not 0 the size and floor pair is appended.</param>
    /// <returns>The packet.</returns>
    public static byte[] MapChange(
        int playerId,
        ushort mapId,
        ushort x,
        ushort y,
        ushort conveyIndex,
        byte houseRoom = 0,
        byte roomSize = 0)
    {
        var w = new PacketWriter(Action12.Code).I32(playerId).U16(mapId).U16(x).U16(y).U16(conveyIndex).U8(houseRoom);
        if (roomSize != 0)
            w.U8(roomSize).U8(houseRoom);
        return w.ToArray();
    }

    /// <summary>Builds the <c>70/1</c> packet (message 23) with the owner guild of the castle.</summary>
    /// <param name="guildName">Name of the owning guild.</param>
    /// <param name="ensignId">Ensign id; int.MaxValue for none.</param>
    /// <returns>The packet.</returns>
    public static byte[] CastleOwner(string guildName = "", int ensignId = int.MaxValue) =>
        new PacketWriter(Action70.Code, Action70.War).U8(Action70.CastleOwner).PStr8(guildName).I32(ensignId).ToArray();

    /// <summary>Builds the <c>70/1</c> packet (message 10) that announces a new castle leader.</summary>
    /// <param name="guildName">Name of the new leader guild, or null for no guild.</param>
    /// <returns>The packet.</returns>
    public static byte[] CastleNewLeader(string? guildName) =>
        new PacketWriter(Action70.Code, Action70.War).U8(Action70.NewLeader).U8(guildName is null ? (byte)2 : (byte)1)
            .PStr8(guildName ?? "").I32(int.MaxValue).ToArray();

    /// <summary>Builds the <c>1/1</c> packet that removes a player from the scene.</summary>
    /// <param name="playerId">Id of the player.</param>
    /// <returns>The packet.</returns>
    public static byte[] PlayerLeft(int playerId) =>
        new PacketWriter(Action1.Code, Action1.PlayerLeft).I32(playerId).ToArray();

    /// <summary>Builds the <c>6/1</c> packet that shows a player walking.</summary>
    /// <param name="playerId">Id of the player.</param>
    /// <param name="direction">Direction byte.</param>
    /// <param name="x">Destination x.</param>
    /// <param name="y">Destination y.</param>
    /// <returns>The packet.</returns>
    public static byte[] PlayerWalk(int playerId, byte direction, ushort x, ushort y) =>
        new PacketWriter(Action6.Code, Action6.WalkTo).I32(playerId).U8(direction).U16(x).U16(y).ToArray();

    /// <summary>Builds the <c>7</c> packet that puts a player on a spot of their map without a walk.</summary>
    /// <param name="playerId">Id of the player.</param>
    /// <param name="mapId">The player's map id.</param>
    /// <param name="x">The spot's x, px.</param>
    /// <param name="y">The spot's y, px.</param>
    /// <returns>The packet.</returns>
    public static byte[] Jump(int playerId, ushort mapId, ushort x, ushort y) =>
        new PacketWriter(Action7.Code).I32(playerId).U16(mapId).U16(x).U16(y).ToArray();

    /// <summary>Builds the <c>2/8</c> packet that tells the client its chat is blocked; it holds its local chat for 8 seconds.</summary>
    /// <returns>The packet.</returns>
    public static byte[] ChatBlocked() => new PacketWriter(Action2.Code, Action2.ChatBlocked).ToArray();

    /// <summary>Builds a chat packet for a channel.</summary>
    /// <param name="channel">Channel (the sub code).</param>
    /// <param name="senderId">Id of the speaker.</param>
    /// <param name="text">The chat text.</param>
    /// <returns>The packet.</returns>
    public static byte[] Chat(byte channel, int senderId, string text) =>
        new PacketWriter(Action2.Code, channel).I32(senderId).RawString(text).ToArray();

    /// <summary>Builds the <c>32/1</c> packet that shows an emotion above a player.</summary>
    /// <param name="playerId">Id of the player.</param>
    /// <param name="emotion">Emotion id.</param>
    /// <returns>The packet.</returns>
    public static byte[] PlayerEmotion(int playerId, byte emotion) =>
        new PacketWriter(Action32.Code, Action32.Emotion).I32(playerId).U8(emotion).ToArray();

    /// <summary>Builds the <c>32/2</c> packet that plays an action frame on a player.</summary>
    /// <param name="playerId">Id of the player.</param>
    /// <param name="frame">Action frame.</param>
    /// <returns>The packet.</returns>
    public static byte[] PlayerAction(int playerId, byte frame) =>
        new PacketWriter(Action32.Code, Action32.Action).I32(playerId).U8(frame).ToArray();

    /// <summary>Builds the <c>10/1</c> packet with the nickname of a player.</summary>
    /// <param name="playerId">Id of the player.</param>
    /// <param name="nickname">The nickname.</param>
    /// <returns>The packet.</returns>
    public static byte[] Nickname(int playerId, string nickname) =>
        new PacketWriter(Action10.Code, Action10.Nickname).I32(playerId).PStr8(nickname).ToArray();

    /// <summary>Builds the <c>10/2</c> packet with the heart number of a scene player.</summary>
    /// <param name="playerId">Id of the player.</param>
    /// <param name="heart">The heart number.</param>
    /// <returns>The packet.</returns>
    public static byte[] HeartNumber(int playerId, byte heart) =>
        new PacketWriter(Action10.Code, Action10.Profile).I32(playerId).U8(heart).ToArray();

    /// <summary>Builds the <c>33/2</c> packet with the option buttons and chat channel bits.</summary>
    /// <param name="option1">State of option 1 (1 on, 2 off).</param>
    /// <param name="option2">State of option 2.</param>
    /// <param name="option6">State of option 6.</param>
    /// <param name="party">Party option; written only for the 2014 client.</param>
    /// <param name="channelBits">Chat channel bits (bit n-1 is channel n).</param>
    /// <param name="client">Client generation, which decides whether the party option is written.</param>
    /// <returns>The packet.</returns>
    public static byte[] OptionState(
        byte option1,
        byte option2,
        byte option6,
        byte party,
        byte channelBits,
        ClientGeneration client)
    {
        var w = new PacketWriter(Action33.Code, Action33.OptionState).U8(option1).U8(option2).U8(option6);
        if (client == ClientGeneration.Client2014)
            w.U8(party);
        return w.U8(channelBits).ToArray();
    }
}
