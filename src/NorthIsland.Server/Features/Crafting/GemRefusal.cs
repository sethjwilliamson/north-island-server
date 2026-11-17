namespace NorthIsland.Server.Features.Crafting;

/// <summary>The reason a stone cannot be mounted in a piece of equipment, or <see cref="None"/>.</summary>
public enum GemRefusal
{
    /// <summary>The stone can be mounted.</summary>
    None,

    /// <summary>The piece already holds a stone.</summary>
    AlreadyMounted,

    /// <summary>The piece is an accessory other than a class manteau.</summary>
    SpecialItem,

    /// <summary>The piece is a gun.</summary>
    Gun,

    /// <summary>The piece is item-mall equipment, which has no socket.</summary>
    MallEquipment,

    /// <summary>A critical stone was meant for something other than a weapon.</summary>
    NotWeapon,

    /// <summary>A critical stone was meant for a weapon that has a critical chance of its own.</summary>
    CriticalWeapon
}
