namespace NorthIsland.Protocol.Opcodes;

/// <summary>Action id 50: battle commands of a round and the actions the server sends back.</summary>
public static class Action50
{
    /// <summary>Action id of this packet group, 50.</summary>
    public const byte Code = 50;

    /// <summary>Sub code for skill commands in a battle: sent by the client, and used for the server's action records.</summary>
    public const byte Skill = 1;

    /// <summary>Client to server: an item command in a battle.</summary>
    public const byte Item = 2;

    /// <summary>Server to client: which cells around its target a fighter's next action reaches.</summary>
    public const byte AttackArea = 6;
}
