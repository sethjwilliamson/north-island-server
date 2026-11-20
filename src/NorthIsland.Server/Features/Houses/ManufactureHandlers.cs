using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NorthIsland.GameData.Items;
using NorthIsland.Protocol.IO;
using NorthIsland.Protocol.Messages.Items;
using NorthIsland.Protocol.Messages.World;
using NorthIsland.Protocol.Opcodes;

namespace NorthIsland.Server.Features.Houses;

/// <summary>
/// Handles manufacturing at the furniture of a player's own house: starting a job from a recipe, continuing, stopping and giving up jobs,
/// delivering the products when the time is up, and the Magic Sandglass and Time Mantle that shorten jobs.
/// </summary>
/// <param name="world">The world state, used to find the sessions in a house and to see who is online.</param>
/// <param name="items">The item and recipe data.</param>
/// <param name="tents">The tent handlers, whose house-entered event pauses the jobs and whose house map check guards the orders.</param>
/// <param name="options">The house options; the house life section holds the Magic Sandglass percent and number of uses.</param>
/// <param name="logger">The logger.</param>
public sealed class ManufactureHandlers(
    WorldState world,
    ItemData items,
    TentHandlers tents,
    IOptions<HouseOptions> options,
    ILogger<ManufactureHandlers> logger)
{
    /// <summary>The most jobs one player can have at the same time (job slots 1 to 5).</summary>
    public const int MaxJobs = 5;

    /// <summary>The most products one order may make (products per batch times batches).</summary>
    public const int MaxProducts = 50;

    /// <summary>Hooks the handlers into the tent house-entered event and the world leaving event.</summary>
    internal void Start()
    {
        tents.HouseEntered += OnHouseEntered;
        world.Leaving += PauseAll;
    }

    /// <summary>
    /// Handles the manufacture order <c>64/1</c>: checks the recipe, the furniture it is made at, the blueprint, the batch count, a free job slot and
    /// the pet, takes the gas, bag items and furniture pieces the recipe needs, then creates the job, tells the client and schedules the delivery.
    /// An order that fails a check or lacks material is logged and refused without taking anything.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code.</param>
    internal ValueTask OnMake(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (InOwnHouse(session) is not { } character || body.Length < 12)
            return ValueTask.CompletedTask;
        var r = new PacketReader(body.Span);
        var (furnIndex, recipeIndex, batches) = (r.U16(), r.U16(), r.U8());
        var (smallFurn, smallGas, canFurn, canGas) = (r.U8(), (int)r.U16(), r.U8(), (int)r.U16());
        var bagParts = new List<(byte Slot, byte Count)>();
        for (int i = 0, n = r.U8(); i < n; i++)
            bagParts.Add((r.U8(), r.U8()));
        var furnParts = new List<ushort>();
        for (int i = 0, n = r.U8(); i < n; i++)
            furnParts.Add(r.U16());
        if (r.Remaining >= 2)
            r.Bytes(2);
        var (petSlot, petId) = ReadPet(ref r);

        var recipe = items.Recipes.FirstOrDefault(x => x.Index == recipeIndex);
        var station = character.Furniture.Find(f => f.Index == furnIndex);
        var why = recipe is null ? "no such recipe"
            : station is null ? "no such furniture"
                : !Makes(character, station, recipe) ? "not made at this furniture"
                    : !BookHandlers.HasBlueprint(character, recipe.Blueprint) ? "the blueprint is missing"
                        : batches == 0 || Math.Max((byte)1, recipe.PerBatch) * batches > MaxProducts ? "bad batch count"
                            : character.MakeJobs.Count >= MaxJobs ? "all job slots busy"
                                : petSlot != 0 && PetRefusal(session, character, petSlot, petId) is { } petWhy ? petWhy
                                    : null;
        if (why is not null)
        {
            logger.LogWarning(
                "[{Session}] {Name}: manufacture recipe {Recipe} at furniture {Furn}: {Why}",
                session.Id,
                character.Name,
                recipeIndex,
                furnIndex,
                why);
            RefusePetOrder(session, petSlot);
            return ValueTask.CompletedTask;
        }

        var need = recipe!.Materials.GroupBy(m => m.Item).ToDictionary(g => g.Key, g => g.Sum(m => Math.Max(1, (int)m.Count)) * batches);
        var smallTaken = smallGas > 0 && character.Furniture.Exists(f => f.Index == smallFurn && f.ItemId == GasHandlers.SmallStorage && !f.Retained)
            ? Math.Min(smallGas, character.GasSmall)
            : 0;
        var canTaken = canGas > 0 && character.Furniture.Exists(f => f.Index == canFurn && f.ItemId == GasHandlers.BigStorage && !f.Retained)
            ? Math.Min(canGas, character.GasCan)
            : 0;
        if (need.TryGetValue(GasHandlers.Gas, out var gasLeft))
        {
            smallTaken = Math.Min(smallTaken, gasLeft);
            canTaken = Math.Min(canTaken, gasLeft - smallTaken);
            need[GasHandlers.Gas] = gasLeft - smallTaken - canTaken;
        }
        else
            (smallTaken, canTaken) = (0, 0);
        var takeBag = new List<(BagItem Stack, byte Count)>();
        foreach (var (slot, count) in bagParts)
        {
            var stack = character.Bag.Find(b => b.Slot == slot);
            if (stack is null || !need.TryGetValue(stack.ItemId, out var left) || left == 0)
                continue;
            var already = takeBag.Where(t => t.Stack == stack).Sum(t => t.Count);
            var take = (byte)Math.Min(Math.Min(count, left), stack.Count - already);
            if (take == 0)
                continue;
            takeBag.Add((stack, take));
            need[stack.ItemId] = left - take;
        }
        var takeFurn = new List<Furniture>();
        foreach (var index in furnParts.Distinct())
        {
            var piece = character.Furniture.Find(f => f.Index == index && f.Index != furnIndex && f.Parent == 0);
            if (piece is null || !need.TryGetValue(piece.ItemId, out var left) || left == 0 || character.Furniture.Exists(f => f.Parent == index))
                continue;
            takeFurn.Add(piece);
            need[piece.ItemId] = left - 1;
        }
        if (need.Values.Any(v => v > 0))
        {
            logger.LogWarning("[{Session}] {Name}: manufacture {Product}: missing {Missing}", session.Id, character.Name, NameOf(recipe.Product),
                string.Join(", ", need.Where(kv => kv.Value > 0).Select(kv => $"{NameOf(kv.Key)} x{kv.Value}")));
            RefusePetOrder(session, petSlot);
            return ValueTask.CompletedTask;
        }

        if (smallTaken > 0)
        {
            character.GasSmall -= (ushort)smallTaken;
            session.Send(HouseMessages.GasState(2, character.GasSmall, finished: false)); // tank
        }
        if (canTaken > 0)
        {
            character.GasCan -= (ushort)canTaken;
            session.Send(HouseMessages.GasState(1, character.GasCan, finished: false));
        }
        foreach (var (stack, count) in takeBag)
        {
            stack.Count -= count;
            if (stack.Count == 0)
                character.Bag.Remove(stack);
            session.Send(ItemMessages.BagSlotDecrease(stack.Slot, count));
        }
        foreach (var piece in takeFurn)
        {
            character.Furniture.Remove(piece);
            SendToHouse(session, HouseMessages.FurnitureRemoved(piece.Index));
        }

        var sandglass = character.SandglassArmed;
        var job = new MakeJob
        {
            Slot = (byte)Enumerable.Range(1, MaxJobs).First(s => !character.MakeJobs.Exists(j => j.Slot == s)),
            Recipe = recipe.Index,
            Product = recipe.Product,
            Batches = batches,
            Sandglass = sandglass,
            TotalMs = (sandglass ? SandglassSeconds(recipe.Minutes, batches, options.Value.HouseLife.SandglassPercent)
                : TotalSeconds(recipe.Minutes, batches, BonusPercent(character.Furniture.Select(f => f.ItemId)))) * 1000,
            StartedUtc = GameClock.UtcNow,
            PetSlot = petSlot,
            PetId = petSlot != 0 ? petId : 0,
        };
        character.MakeJobs.Add(job);
        session.Send(HouseMessages.MakeJobs([RecordOf(job, GameClock.UtcNow)]));
        session.Send(HouseMessages.MakeState(Action64.Start, job.Slot));
        SendPetMaker(session, job);
        if (sandglass)
        {
            character.SandglassArmed = false;
            SendSandglassFlags(session, character);
        }
        logger.LogInformation(
            "[{Session}] {Name} manufactures {Product} x{Batches} batch(es) at {Station} (recipe {Recipe}, {Minutes} min each, {Total} s in all) in slot {Slot}",
            session.Id, character.Name, NameOf(recipe.Product), batches, NameOf(station!.ItemId), recipe.Index, recipe.Minutes, job.TotalMs / 1000, job.Slot);
        Schedule(session, job);
        return ValueTask.CompletedTask;
    }

    /// <summary>Returns the percent by which the placed furniture shortens manufacture times; each kind of furniture counts once.</summary>
    /// <param name="furniture">The item ids of the furniture placed in the house.</param>
    /// <returns>The total bonus in percent.</returns>
    public static int BonusPercent(IEnumerable<ushort> furniture) =>
        furniture.Distinct().Sum(id => id switch
        {
            39053 => 15, // Air Condition
            39007 => 2, // Wood Bell
            39045 => 3, // Stand Bell
            37202 => 3, // Windmill
            _ => 0 // any other furniture gives no bonus
        });

    /// <summary>Returns the extra products per batch that a planting recipe gets from the furniture placed around its bed.</summary>
    /// <param name="furniture">The furniture placed in the house.</param>
    /// <param name="facility">The item id of the facility the recipe is made at.</param>
    /// <param name="product">The item id of the recipe's product.</param>
    /// <returns>The extra products per batch; 0 when the recipe gets none.</returns>
    public static int ExtraPerBatch(IReadOnlyList<Furniture> furniture, ushort facility, ushort product)
    {
        if (product == 30256 || facility is not (39075 or 39082) // 30256 Halloween Pumpkin, 39075 Turf, 39082 Parterre
            || furniture.Where(f => f.ItemId == facility).OrderBy(f => f.Index).FirstOrDefault() is not { } bed)
            return 0;
        bool Near(params ushort[] kinds) => furniture.Any(f => kinds.Contains(f.ItemId) && f.Room == bed.Room
            && Math.Abs(f.X - bed.X) + Math.Abs(f.Y - bed.Y) <= 6);
        return (Near(facility == 39075 ? (ushort)39082 : (ushort)39075) ? 10 : 0) // 39075 Turf, 39082 Parterre, 39075 Turf
            + (Near(39077) ? 10 : 0) // Fence
            + (Near(39098, 39172) ? 10 : 0); // 39098 French Window, 39172 French Window
    }

    /// <summary>Returns the time of a job in seconds: the recipe minutes times the batches, shortened by the furniture bonus, at least 1 when timed.</summary>
    /// <param name="minutes">The recipe's minutes per batch.</param>
    /// <param name="batches">The number of batches ordered.</param>
    /// <param name="bonusPercent">The furniture bonus in percent.</param>
    /// <returns>The time of the whole job in seconds; 0 when the recipe takes no time.</returns>
    public static int TotalSeconds(int minutes, int batches, int bonusPercent)
    {
        var seconds = minutes * 60 * batches;
        return seconds == 0 ? 0 : Math.Max(1, (100 - bonusPercent) * seconds / 100);
    }

    /// <summary>Returns the time of a job under the Magic Sandglass in seconds.</summary>
    /// <param name="minutes">The recipe's minutes per batch.</param>
    /// <param name="batches">The number of batches ordered.</param>
    /// <param name="percent">The share of the normal time the job takes under the sandglass, in percent.</param>
    /// <returns>The time of the whole job in seconds; 0 when the recipe takes no time.</returns>
    public static int SandglassSeconds(int minutes, int batches, int percent)
    {
        var seconds = minutes * 60 * batches;
        return seconds == 0 ? 0 : Math.Max(1, seconds * percent / 100);
    }

    /// <summary>
    /// Builds the sandglass flags byte of a character: bit 5 (value 32) while a sandglass use is armed, and bit <c>slot - 1</c> for each job slot
    /// whose job runs under a sandglass.
    /// </summary>
    /// <param name="character">The character whose jobs are looked at.</param>
    /// <returns>The flags byte.</returns>
    public static byte SandglassFlags(Character character)
    {
        var flags = character.SandglassArmed ? 32 : 0;
        foreach (var job in character.MakeJobs.Where(j => j.Sandglass && j.Slot is >= 1 and <= MaxJobs))
            flags |= 1 << (job.Slot - 1);
        return (byte)flags;
    }

    /// <summary>Sends the character's current sandglass flags to the player.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="character">The player's active character.</param>
    private static void SendSandglassFlags(GameSession session, Character character) =>
        session.Send(HouseLifeMessages.SandglassFlags(SandglassFlags(character)));

    /// <summary>Returns the value shown for a sandglass piece: its uses so far, scaled so that a fully used piece reaches 250.</summary>
    /// <param name="piece">The sandglass furniture piece.</param>
    /// <returns>The value for the client, at most 250.</returns>
    private byte SandglassValue(Furniture piece) =>
        (byte)Math.Min(250, piece.Uses * (250 / Math.Max(1, options.Value.HouseLife.SandglassUses)));

    /// <summary>
    /// Handles using the Magic Sandglass (<c>62/53</c>): arms one use for the next order and tells the owner. A missing piece, an already armed use
    /// or a used-up piece is logged and ignored.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code.</param>
    internal ValueTask OnSandglass(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (InOwnHouse(session) is not { } character || body.Length < 2)
            return ValueTask.CompletedTask;
        var piece = character.Furniture.Find(f => f.Index == body.Span[1] && f.ItemId is 39074 or 39097); // 39074 Magic Sandglass, 39097 Magic Sandglass
        if (piece is null || character.SandglassArmed || piece.Uses >= options.Value.HouseLife.SandglassUses)
        {
            logger.LogInformation(
                "[{Session}] {Name}: sandglass {Furn} refused (armed {Armed})",
                session.Id,
                character.Name,
                body.Span[1],
                character.SandglassArmed);
            return ValueTask.CompletedTask;
        }
        piece.Uses++;
        character.SandglassArmed = true;
        session.Send(HouseLifeMessages.SandglassUsed((byte)piece.Index, SandglassValue(piece)));
        SendSandglassFlags(session, character);
        logger.LogInformation(
            "[{Session}] {Name} uses the Magic Sandglass ({Used}/{Max})",
            session.Id,
            character.Name,
            piece.Uses,
            options.Value.HouseLife.SandglassUses);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Handles using a Time Mantle from the bag (<c>23/108</c>): at home, with at least one job running, the mantle is used up and every running job
    /// is delivered at once.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code.</param>
    internal ValueTask OnTimeMantle(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (InOwnHouse(session) is not { } character || body.Length < 1 || session.Battle is not null)
            return ValueTask.CompletedTask;
        var mantle = character.Bag.Find(b => b.Slot == body.Span[0] && b.ItemId == 34168); // Time Mantle
        var running = character.MakeJobs.Where(j => j.StartedUtc is not null).ToList();
        if (mantle is null || running.Count == 0)
            return ValueTask.CompletedTask;
        mantle.Count--;
        if (mantle.Count == 0)
            character.Bag.Remove(mantle);
        session.Send(ItemMessages.BagSlotDecrease(mantle.Slot, 1));
        session.Send(ItemMessages.UsedSound());
        foreach (var job in running)
            Deliver(session, job);
        logger.LogInformation("[{Session}] {Name} uses a Time Mantle: {Count} job(s) finished", session.Id, character.Name, running.Count);
        return ValueTask.CompletedTask;
    }

    /// <summary>Sends the pet-refused packet when the order named a pet, so the client can walk again.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="petSlot">The pet slot the order named; 0 when it named none.</param>
    private static void RefusePetOrder(GameSession session, byte petSlot)
    {
        if (petSlot != 0)
            session.Send(HouseMessages.PetRefused());
    }

    /// <summary>
    /// Handles <c>64/2</c>, which continues the job in a slot: a job that has not started yet is started (with the pet the packet names, if any),
    /// and its delivery is scheduled. A slot without a job is logged and answered with the pet-left packet.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code.</param>
    internal ValueTask OnContinue(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (InOwnHouse(session) is not { } character || body.Length < 3)
            return ValueTask.CompletedTask;
        if (JobIn(character, body.Span[2]) is not { } job)
        {
            logger.LogWarning("[{Session}] {Name}: continue manufacture: no job in slot {Slot}", session.Id, character.Name, body.Span[2]);
            session.Send(HouseMessages.PetLeft());
            return ValueTask.CompletedTask;
        }
        var r = new PacketReader(body.Span[3..]);
        if (r.Remaining >= 2)
            r.Bytes(2);
        var (petSlot, petId) = ReadPet(ref r);
        if (job.StartedUtc is null)
        {
            if (petSlot != 0 && PetRefusal(session, character, petSlot, petId) is { } why)
            {
                logger.LogWarning("[{Session}] {Name}: continue manufacture with pet {Pet}: {Why}", session.Id, character.Name, petId, why);
                session.Send(HouseMessages.PetLeft());
                return ValueTask.CompletedTask;
            }
            (job.PetSlot, job.PetId) = (petSlot, petSlot != 0 ? petId : 0);
            job.StartedUtc = GameClock.UtcNow;
            session.Send(HouseMessages.MakeJobs([RecordOf(job, GameClock.UtcNow)]));
            session.Send(HouseMessages.MakeState(Action64.Start, job.Slot));
            SendPetMaker(session, job);
        }
        Schedule(session, job);
        return ValueTask.CompletedTask;
    }

    /// <summary>Handles <c>64/3</c>: pauses the job in the named slot and confirms it to the client.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code.</param>
    internal static ValueTask OnStop(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (InWorld(session) is not { } character || body.Length < 1 || JobIn(character, body.Span[0]) is not { } job)
            return ValueTask.CompletedTask;
        Pause(job);
        session.Send(HouseMessages.MakeState(Action64.Stop, job.Slot));
        return ValueTask.CompletedTask;
    }

    /// <summary>Handles <c>64/4</c> from the client: drops the job in the named slot without a product and confirms it.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code.</param>
    internal ValueTask OnGiveUp(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (InWorld(session) is not { } character || body.Length < 1 || JobIn(character, body.Span[0]) is not { } job)
            return ValueTask.CompletedTask;
        character.MakeJobs.Remove(job);
        session.Send(HouseMessages.MakeState(Action64.GivenUp, job.Slot));
        if (job.Sandglass)
            SendSandglassFlags(session, character);
        logger.LogInformation("[{Session}] {Name} gives up manufacturing {Product}", session.Id, character.Name, NameOf(job.Product));
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Runs when a player enters a house. For the owner it sends the used sandglass pieces and flags, pauses every job and sends the job list, so
    /// the client can continue them.
    /// </summary>
    /// <param name="session">The session of the player who entered.</param>
    /// <param name="owner">The session of the house's owner; null when the owner is not online.</param>
    private void OnHouseEntered(GameSession session, GameSession? owner)
    {
        if (owner != session || session.ActiveCharacter is not { } character)
            return;
        var now = GameClock.UtcNow;
        foreach (var piece in character.Furniture.Where(f => f.ItemId is 39074 or 39097 && f.Uses > 0)) // 39074 Magic Sandglass, 39097 Magic Sandglass
            session.Send(HouseLifeMessages.SandglassUsed((byte)piece.Index, SandglassValue(piece)));
        if (SandglassFlags(character) != 0)
            SendSandglassFlags(session, character);
        if (character.MakeJobs.Count == 0)
            return;
        foreach (var job in character.MakeJobs)
            Pause(job);
        session.Send(HouseMessages.MakeJobs(character.MakeJobs.Select(j => RecordOf(j, now))));
    }

    /// <summary>Delivers the job when its time is up now, or after the remaining time unless it has been paused or removed or the player has left.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="job">The job to deliver.</param>
    private void Schedule(GameSession session, MakeJob job)
    {
        var left = job.TotalMs - job.ElapsedNow(GameClock.UtcNow);
        if (left <= 0)
        {
            Deliver(session, job);
            return;
        }
        var started = job.StartedUtc;
        _ = DeliverLaterAsync(session, job, started, left);
    }

    /// <summary>Waits out a job's time, then delivers it while holding the player's state, unless it was paused, restarted or the player left.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="job">The job to deliver.</param>
    /// <param name="started">The job's start when it was scheduled.</param>
    /// <param name="left">The milliseconds left.</param>
    private async Task DeliverLaterAsync(GameSession session, MakeJob job, DateTime? started, int left)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(left)).ConfigureAwait(false);
            using var exclusive = await session.ExclusiveAsync().ConfigureAwait(false);
            if (job.StartedUtc == started && session.ActiveCharacter?.MakeJobs.Contains(job) == true && world.IsOnline(session.Account?.LoginId ?? 0))
                Deliver(session, job);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[{Session}] manufacture delivery failed", session.Id);
        }
    }

    /// <summary>
    /// Finishes a job: puts the products into the bag and removes the job. When the bag cannot hold them the job stays, fully elapsed and paused,
    /// and the client is told the bag is full.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="job">The job to deliver.</param>
    private void Deliver(GameSession session, MakeJob job)
    {
        var character = session.ActiveCharacter!;
        if (job.PetId != 0 && !character.Pets.Exists(p => p.NpcId == job.PetId))
        {
            (job.PetSlot, job.PetId) = (0, 0);
            session.Send(HouseMessages.PetLeft());
        }
        var recipe = items.Recipes.FirstOrDefault(r => r.Index == job.Recipe);
        var count = (Math.Max((byte)1, recipe?.PerBatch ?? 1) + (recipe is null ? 0 : ExtraPerBatch(character.Furniture, recipe.Facility, recipe.Product)))
            * job.Batches;
        var trial = character.Bag.Select(
            b => new BagItem { Slot = b.Slot, ItemId = b.ItemId, Count = b.Count, Wear = b.Wear, Fusion = b.Fusion, Pose = b.Pose, BoxItem = b.BoxItem })
            .ToList();
        if (Bag.Add(trial, items, job.Product, count).Sum(c => c.Added) < count)
        {
            job.ElapsedMs = job.TotalMs;
            job.StartedUtc = null;
            session.Send(HouseMessages.MakeState(Action64.BagFull, job.Slot));
            logger.LogInformation("[{Session}] {Name}: {Product} x{Count} made, but the bag is full", session.Id, character.Name, NameOf(job.Product), count);
            return;
        }
        foreach (var (stack, added) in Bag.Add(character, items, job.Product, count))
            session.Send(ItemMessages.BagAdd(new BagEntry { Slot = stack.Slot, ItemId = stack.ItemId, Count = added, Wear = stack.Wear }));
        character.MakeJobs.Remove(job);
        session.Send(HouseMessages.MakeState(Action64.Finish, job.Slot));
        if (job.Sandglass)
            SendSandglassFlags(session, character);
        logger.LogInformation("[{Session}] {Name} finishes {Product} x{Count}", session.Id, character.Name, NameOf(job.Product), count);
    }

    /// <summary>Pauses every job of the player's active character.</summary>
    /// <param name="session">The player's session.</param>
    private void PauseAll(GameSession session)
    {
        foreach (var job in session.ActiveCharacter?.MakeJobs ?? [])
            Pause(job);
    }

    /// <summary>Stops the job's clock and keeps the time done so far.</summary>
    /// <param name="job">The job to pause.</param>
    private static void Pause(MakeJob job)
    {
        job.ElapsedMs = job.ElapsedNow(GameClock.UtcNow);
        job.StartedUtc = null;
    }

    /// <summary>Returns whether the item is a Maid Robot or a Wit Maid; with one placed in the house, a pet can make things.</summary>
    /// <param name="itemId">The item id of a furniture piece.</param>
    /// <returns>True when it is a maid piece.</returns>
    public static bool IsMaid(ushort itemId) =>
        itemId is 39066 // Maid Robot
            or 39096 // Wit Maid
            or >= 39117 and <= 39119; // 39117 Wit Maid-Summer, 39119 Wit Maid-Cat

    /// <summary>Reads the optional pet at the end of an order: a slot (u8) and a pet id (i32).</summary>
    /// <param name="r">The reader, positioned after the rest of the order.</param>
    /// <returns>The pet slot and pet id; both 0 when the packet holds no pet.</returns>
    private static (byte Slot, int Id) ReadPet(ref PacketReader r) => r.Remaining >= 5 ? (r.U8(), r.I32()) : ((byte)0, 0);

    /// <summary>Returns why the pet cannot make things, or null when it can.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="character">The player's active character.</param>
    /// <param name="petSlot">The pet slot the order named.</param>
    /// <param name="petId">The npc id of the pet the order named.</param>
    /// <returns>The reason as log text; null when the pet may make things.</returns>
    private static string? PetRefusal(GameSession session, Character character, byte petSlot, int petId) =>
        !character.Furniture.Exists(f => IsMaid(f.ItemId)) ? "no Maid in the house"
            : character.Pets.Find(p => p.NpcId == petId) is not { } pet ? "no such pet"
                : character.ActivePetId == petId ? "the pet is fighting"
                    : session.RidingPetSlot != 0 && session.RidingPetSlot == pet.Slot ? "the pet is ridden"
                        : petSlot is < 1 or > 4 ? "bad pet slot"
                            : null;

    /// <summary>Tells the client which pet makes the job; sends nothing when the job has no pet.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="job">The job that was started.</param>
    private void SendPetMaker(GameSession session, MakeJob job)
    {
        if (job.PetId == 0)
            return;
        session.Send(HouseMessages.PetMaker(job.PetSlot, job.PetId));
        logger.LogInformation("[{Session}] pet {Pet} (slot {Slot}) makes {Product}", session.Id, job.PetId, job.PetSlot, NameOf(job.Product));
    }

    /// <summary>Returns whether the recipe is made at the station: the station is the recipe's facility or carries it as a tool.</summary>
    /// <param name="character">The player's active character.</param>
    /// <param name="station">The furniture piece the order was given at.</param>
    /// <param name="recipe">The recipe.</param>
    /// <returns>True when the recipe can be made there.</returns>
    private static bool Makes(Character character, Furniture station, CompoundRecipe recipe) =>
        recipe.Facility == station.ItemId || character.Furniture.Exists(f => f.Parent == station.Index && f.ItemId == recipe.Facility);

    /// <summary>Finds the job in a job slot.</summary>
    /// <param name="character">The player's active character.</param>
    /// <param name="slot">The job slot.</param>
    /// <returns>The job; null when the slot is empty.</returns>
    private static MakeJob? JobIn(Character character, byte slot) => character.MakeJobs.Find(j => j.Slot == slot);

    /// <summary>Builds the job record the client is sent.</summary>
    /// <param name="job">The job.</param>
    /// <param name="now">The current time, used for the time done so far.</param>
    /// <returns>The record.</returns>
    private static MakeJobRecord RecordOf(MakeJob job, DateTime now) => new(job.Slot, job.Product, job.ElapsedNow(now), job.Batches);

    /// <summary>Returns the name of an item for the log, or <c>item N</c> when it is unknown.</summary>
    /// <param name="id">The item id.</param>
    /// <returns>The item name.</returns>
    private string NameOf(ushort id) => items[id]?.Name ?? $"item {id}";

    /// <summary>Sends a packet to everyone in the house the player is in, and to the player.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="packet">The encoded packet.</param>
    private void SendToHouse(GameSession session, byte[] packet)
    {
        foreach (var s in world.OnMap(session.ActiveCharacter!.MapId).Append(session).Distinct())
            s.Send(packet);
    }

    /// <summary>Returns the active character of a session that is in the world.</summary>
    /// <param name="session">The player's session.</param>
    /// <returns>The character; null when the session is not in the world.</returns>
    private static Character? InWorld(GameSession session)
    {
        var character = session.ActiveCharacter;
        return session.State == SessionState.InWorld && character is not null && session.Account is not null ? character : null;
    }

    /// <summary>Returns the active character of a session that stands in its own house.</summary>
    /// <param name="session">The player's session.</param>
    /// <returns>The character; null when the session is not in the world or not in its own house.</returns>
    private static Character? InOwnHouse(GameSession session) =>
        InWorld(session) is { } character && TentHandlers.HouseMapOf(session.Account!.LoginId) == character.MapId ? character : null;
}
