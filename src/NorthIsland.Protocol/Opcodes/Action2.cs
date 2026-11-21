namespace NorthIsland.Protocol.Opcodes;

/// <summary>Action 2: chat.</summary>
public static class Action2
{
    /// <summary>Action code 2; the sub code is the chat channel.</summary>
    public const byte Code = 2;

    /// <summary>World chat channel.</summary>
    public const byte World = 1;

    /// <summary>Local (same map) chat channel.</summary>
    public const byte Local = 2;

    /// <summary>Whisper channel; the packet starts with the id of the receiver.</summary>
    public const byte Whisper = 3;

    /// <summary>Chat channel 4, which the server does not relay.</summary>
    public const byte Channel4 = 4;

    /// <summary>Team chat channel.</summary>
    public const byte Team = 5;

    /// <summary>Guild chat channel.</summary>
    public const byte Guild = 6;

    /// <summary>Chat channel 7, which the server does not relay.</summary>
    public const byte Channel7 = 7;

    /// <summary>Server to client: the player's chat is blocked; the client holds its local chat for 8 seconds.</summary>
    public const byte ChatBlocked = 8;
}
