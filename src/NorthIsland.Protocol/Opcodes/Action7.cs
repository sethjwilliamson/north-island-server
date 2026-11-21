namespace NorthIsland.Protocol.Opcodes;

/// <summary>Action <c>7</c>: the jump packet, which puts a player on a spot of their map (the client's Action07Jump).</summary>
public static class Action7
{
    /// <summary>The action byte <c>7</c> that starts the packet; it has no sub code.</summary>
    public const byte Code = 7;
}
