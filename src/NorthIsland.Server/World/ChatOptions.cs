namespace NorthIsland.Server.World;

/// <summary>The limits of chat: the length of a line, how many lines a player may send in a while, and where whispers are refused.</summary>
public sealed class ChatOptions
{
    /// <summary>The name of the section under the server section that holds these settings.</summary>
    public const string Section = "Chat";

    /// <summary>
    /// The most bytes of text one chat line may carry; a longer line is dropped. The client's input box takes 60 characters
    /// (views/chat/InputBar.xml), each up to 3 bytes. 0 turns the check off.
    /// </summary>
    public int MaxTextBytes { get; set; } = 180;

    /// <summary>The most lines a player (not a GM) may send within <see cref="BurstSeconds"/>; 0 turns the limit off.</summary>
    public int BurstLines { get; set; } = 5;

    /// <summary>The window, in seconds, that <see cref="BurstLines"/> counts lines in.</summary>
    public int BurstSeconds { get; set; } = 5;

    /// <summary>The seconds a player who went over the limit stays silenced (the client holds its local chat for 8 seconds after a 2/8).</summary>
    public int BlockSeconds { get; set; } = 8;

    /// <summary>The maps whispers cannot be sent from (the client's kNoWhisperMap).</summary>
    public ushort[] NoWhisperMaps { get; set; } = [10701]; // map 10701: whispering refused (name not in the tables)
}
