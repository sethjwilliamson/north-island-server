namespace NorthIsland.Protocol.Opcodes;

/// <summary>Action 20: events (NPC and door triggers, dialogs and results).</summary>
public static class Action20
{
    /// <summary>Action code 20.</summary>
    public const byte Code = 20;

    /// <summary>Client to server: trigger kind for doors.</summary>
    public const byte DoorTrigger = 8;

    /// <summary>Client to server: asks for the next event result.</summary>
    public const byte RequestNext = 6;

    /// <summary>Client to server: carries a dialog answer.</summary>
    public const byte DialogAnswer = 9;

    /// <summary>Server to client: the event is finished.</summary>
    public const byte Finished = 8;

    /// <summary>Server to client: the server takes over the event.</summary>
    public const byte ServerStart = 12;

    /// <summary>Server to client: a treasure jump for a map NPC.</summary>
    public const byte TreasureJump = 46;

    /// <summary>Server to client: the client stops waiting for the server.</summary>
    public const byte StopWaiting = 9;

    /// <summary>Server to client: the buy-level flow, whose choice 4 is a notice that the function is not available.</summary>
    public const byte BuyLevelFlow = 58;

    /// <summary>The choice of <see cref="BuyLevelFlow"/> that the client shows as "At present, the function is not available".</summary>
    public const byte FunctionUnavailable = 4;
}
