namespace NorthIsland.Protocol.Messages.Events;

/// <summary>Codes of the 85/2 packet: the answer to creating an instance, and the notices sent while one runs.</summary>
public static class InstanceInitCodes
{
    /// <summary>The instance was created.</summary>
    public const byte Created = 0;

    /// <summary>No free copy of the instance is left.</summary>
    public const byte NoCopy = 1;

    /// <summary>The player's level is too low.</summary>
    public const byte Level = 2;

    /// <summary>The player already used the instance today.</summary>
    public const byte DoneToday = 3;

    /// <summary>The instance was completed; the rewards stay open for 10 minutes.</summary>
    public const byte Completed = 4;

    /// <summary>The instance will be deleted in 5 minutes.</summary>
    public const byte Closing = 5;

    /// <summary>The instance was deleted.</summary>
    public const byte Deleted = 6;

    /// <summary>The time ran out.</summary>
    public const byte Expired = 7;

    /// <summary>The player cannot be teleported.</summary>
    public const byte CannotTeleport = 8;

    /// <summary>The player is too busy to be moved into the instance (the client shows "Players are too busy to transmit instance").</summary>
    public const byte TooBusy = 9;

    /// <summary>The instance is closed.</summary>
    public const byte Closed = 11;

    /// <summary>The creation failed.</summary>
    public const byte Failed = 255;
}
