namespace NorthIsland.Server.Features.Crafting;

/// <summary>The rules a compound follows, set by the alchemy skill it is made with.</summary>
public enum AlchemyForm
{
    /// <summary>Primary Alchemy or no skill: the result follows the main material and is always above the lowest material's rank.</summary>
    Primary,

    /// <summary>Junior Alchemy: the result follows the strongest class of the materials and can be up to 8 ranks below the lowest material.</summary>
    Junior,

    /// <summary>Superior Alchemy: as Junior Alchemy, and with a book a failure keeps the materials more often.</summary>
    Superior,

    /// <summary>A blessing: a result of any material's class.</summary>
    Bless,
}
