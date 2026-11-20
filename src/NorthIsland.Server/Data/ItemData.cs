using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NorthIsland.GameData.Items;

namespace NorthIsland.Server.Data;

/// <summary>The item table (Item.dat) and the compound recipes (Compound2.dat), each loaded on first use.</summary>
public sealed class ItemData
{
    /// <summary>The item table, or null when Item.dat is missing; loaded on first access.</summary>
    private readonly Lazy<ItemTable?> _table;

    /// <summary>Sets up lazy loading of Item.dat and Compound2.dat from the game data directory.</summary>
    /// <param name="options">Server options; they name the game data directory.</param>
    /// <param name="logger">Logs what was loaded or missing.</param>
    public ItemData(
        IOptions<ServerOptions> options,
        ILogger<ItemData> logger)
    {
        var path = Path.Combine(ServerPaths.Resolve(options.Value.GameDataDirectory), "Item.dat");
        _table = new Lazy<ItemTable?>(() =>
        {
            if (!File.Exists(path))
            {
                logger.LogWarning("Item data not found at {Path}; equipping is disabled", path);
                return null;
            }
            var table = ItemTable.Open(path);
            logger.LogInformation("Loaded {Count} items from {Path}", table.Items.Count, path);
            table = EquipmentClasses.Apply(table, EquipmentClasses.Load(), out var given);
            logger.LogInformation("{Count} pieces of equipment got their alchemy class from the equipment class tables", given);
            return table;
        });
        var compoundPath = Path.Combine(ServerPaths.Resolve(options.Value.GameDataDirectory), "Compound2.dat");
        _compound = new Lazy<CompoundTable?>(() =>
        {
            if (!File.Exists(compoundPath))
            {
                logger.LogWarning("Recipes not found at {Path}; compounding is disabled", compoundPath);
                return null;
            }
            var table = CompoundTable.Open(compoundPath);
            logger.LogInformation("Loaded {Count} recipes from {Path}", table.Recipes.Count, compoundPath);
            return table;
        });
    }

    /// <summary>Looks up an item.</summary>
    /// <param name="id">The item id.</param>
    public ItemRecord? this[ushort id] => _table.Value?[id];

    /// <summary>Every item of the table; empty without Item.dat.</summary>
    public IReadOnlyList<ItemRecord> All => _table.Value?.Items ?? [];

    /// <summary>
    /// Finds the item that recycling <paramref name="item"/> yields: another item of the same alchemy class with a shop column of 1 or less,
    /// preferring the same type, then the lowest shop column, then the lowest id.
    /// </summary>
    /// <param name="item">The item to recycle.</param>
    /// <returns>The product, or null when the item has a shop column of 1 or less or no alchemy class, or no other item shares its class.</returns>
    public ItemRecord? RecycleProduct(ItemRecord item)
    {
        if (item.ShopColumn <= 1 || Alchemy.Class(item, 0) == 0)
            return null;
        var cls = Alchemy.Class(item, 0);
        return All.Where(r => r.ShopColumn <= 1 && Alchemy.Class(r, 0) == cls && r.Id != item.Id)
            .OrderBy(r => r.Type == item.Type ? 0 : 1).ThenBy(r => r.ShopColumn).ThenBy(r => r.Id).FirstOrDefault();
    }

    /// <summary>The compound recipe table, or null when Compound2.dat is missing; loaded on first access.</summary>
    private readonly Lazy<CompoundTable?> _compound;

    /// <summary>All compound recipes.</summary>
    public IReadOnlyList<CompoundRecipe> Recipes => _compound.Value?.Recipes ?? [];
}
