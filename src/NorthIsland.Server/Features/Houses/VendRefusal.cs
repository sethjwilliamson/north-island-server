using NorthIsland.Protocol.IO;
using NorthIsland.Protocol.Opcodes;

namespace NorthIsland.Server.Features.Houses;

/// <summary>
/// The packet <c>62/25</c> that refuses a request at a shelf, a vending machine or an egg machine, and its codes, which the client turns into a
/// notice (FurnitureStalls::refuse).
/// </summary>
internal static class VendRefusal
{
    /// <summary>Code 1: the client shows "The number of this furniture meets upper limit".</summary>
    public const byte AtLimit = 1;

    /// <summary>Code 2: the client shows "There's no egg in it".</summary>
    public const byte NoEgg = 2;

    /// <summary>Code 3: the client shows "The price has changed, cannot be used".</summary>
    public const byte PriceChanged = 3;

    /// <summary>Code 5: the client shows "It's full" (no room in the bag).</summary>
    public const byte Full = 5;

    /// <summary>Code 7: the client shows "It's full!" (no free row in the vending machine).</summary>
    public const byte MachineFull = 7;

    /// <summary>Builds the packet <c>62/25</c> that refuses a request.</summary>
    /// <param name="code">The refusal code, one of the constants of this class.</param>
    /// <returns>The packet.</returns>
    public static byte[] Packet(byte code) => new PacketWriter(Action62.Code, Action62.VendRefused).U8(code).ToArray();
}
