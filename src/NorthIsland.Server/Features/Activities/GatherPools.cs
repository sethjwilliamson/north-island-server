using System.Collections.Frozen;

namespace NorthIsland.Server.Features.Activities;

/// <summary>The resources of the gathering spots: the skill each kind of spot needs and the items each spot can give.</summary>
public static class GatherPools
{
    /// <summary>Skill id of Collection.</summary>
    public const ushort Collection = 15983;

    /// <summary>Skill id of Cutting.</summary>
    public const ushort Cutting = 15984;

    /// <summary>Skill id of Mining.</summary>
    public const ushort Mining = 15985;

    /// <summary>The materials used by more than one pool.</summary>
    private const ushort Grass = 40014, Cotton = 40007, Harl = 40008, Vine = 40011, VineGrass = 40012, Wood = 45005, Lauan = 45007, Oak = 45006,
        Cypress = 45008, Clay = 44001, RedClay = 44002, BlackClay = 44003, WhiteClay = 44004, YellowClay = 44005, GreyClay = 44006, DryClay = 44007,
        Stone = 43001, Shale = 43002, Sulfur = 42043, Quartz = 42058, CopperOre = 42008, IronOre = 42014, PureIronOre = 42020, AluminumOre = 42033,
        Bluestone = 42074, CrudeOil = 47001;

    /// <summary>
    /// The items of each pool, by the spot's kind and tier. They follow the material lists in the names of the game's events, with an item
    /// listed more than once to make it more common. Bluestone is the exception: no event name lists it, but the original game gave it in two
    /// caves (by the fan wiki and WLOHub), Oslya's fourth cave (map 11047, the crude oil spot, where it was rare) and the Mayan Forest cave
    /// behind Shasha's door (map 12161, with silvery ore and shale).
    /// </summary>
    private static readonly FrozenDictionary<(int Kind, int Tier), ushort[]> Pools = new Dictionary<(int Kind, int Tier), ushort[]>
    {
        [(1, 1)] = [Grass, Cotton, Harl, Vine, VineGrass],
        [(1, 2)] = [Grass, Harl, Vine, 40013], // Paper Grass
        [(1, 3)] = [40016, 40020, Grass], // Bamboo, Pine
        [(2, 1)] = [Lauan, Wood],
        [(2, 2)] = [Wood, Lauan, Oak],
        [(2, 3)] = [Wood, Cypress],
        [(2, 4)] = [Wood, Oak],
        [(3, 1)] = [Clay, WhiteClay, GreyClay, YellowClay, RedClay],
        [(3, 2)] = [Sulfur, WhiteClay, YellowClay, DryClay],
        [(3, 3)] = [Quartz, YellowClay, DryClay],
        [(3, 4)] = [BlackClay, GreyClay, DryClay, Stone],
        [(4, 1)] = [Stone, 42038, Sulfur, Shale, 43007, 43004], // Coal Ore, Granite, Mica Rock
        [(5, 1)] = [CopperOre, 42024, 42028, AluminumOre, IronOre], // Tin Ore, Galena
        [(5, 2)] = [PureIronOre, CopperOre, RedClay],
        [(5, 3)] = [CopperOre, IronOre, PureIronOre, AluminumOre, Quartz],
        [(5, 4)] = [42001, 42057, BlackClay], // Gold Ore, Titanium
        [(5, 5)] = [42005, 42054, Shale, Bluestone], // Silvery Ore, White Silvery Ore
        [(6, 1)] = [CrudeOil, CrudeOil, CrudeOil, CrudeOil, BlackClay, BlackClay, BlackClay, BlackClay, Bluestone], // Bluestone 1 in 9
        [(7, 1)] = [60001], // Sea Water
        [(7, 2)] = [60002], // Fresh Water
        [(8, 1)] = [42050, RedClay, BlackClay], // White Crystal
        [(8, 2)] = [42051, RedClay, BlackClay], // Pink Crystal
        [(8, 3)] = [42049, RedClay, BlackClay], // Amethyst
    }.ToFrozenDictionary();

    /// <summary>Tells which skill a kind of spot needs.</summary>
    /// <param name="kind">The kind of the spot.</param>
    /// <returns>The skill id; 0 for an unknown kind.</returns>
    public static ushort SkillFor(int kind) => kind switch
    {
        1 or 7 => Collection, // plants and water
        2 => Cutting, // wood
        3 or 4 or 5 or 6 or 8 => Mining, // clay, stone, ore, oil and crystal
        _ => 0,
    };

    /// <summary>Picks one item of a pool, with equal odds.</summary>
    /// <param name="kind">The kind of the spot.</param>
    /// <param name="tier">The tier of the spot.</param>
    /// <param name="next">Gives a number from 0 up to but not including its argument.</param>
    /// <returns>The item id; null when there is no such pool.</returns>
    public static ushort? Pick(int kind, int tier, Func<int, int> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return Pools.TryGetValue((kind, tier), out var pool) ? pool[next(pool.Length)] : null;
    }

    /// <summary>Lists every item the pools can give.</summary>
    public static IEnumerable<ushort> AllItems => Pools.Values.SelectMany(p => p).Distinct();
}
