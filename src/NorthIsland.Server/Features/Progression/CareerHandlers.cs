using Microsoft.Extensions.Logging;
using NorthIsland.Protocol.Messages.Events;
using NorthIsland.Protocol.Messages.Items;
using NorthIsland.Protocol.Messages.Player;
using NorthIsland.Protocol.Opcodes;

namespace NorthIsland.Server.Features.Progression;

/// <summary>Handles the career form: opening it, choosing a job (the rebirth) and changing the job of a reborn character.</summary>
/// <param name="items">Item data, for manteau lookups.</param>
/// <param name="formulas">Formula data for the rebirth recalculation.</param>
/// <param name="combat">Recalculates and sends combat stats.</param>
/// <param name="marks">Sets the trial mark flag.</param>
/// <param name="world">World state, for broadcasts.</param>
/// <param name="logger">Logger.</param>
public sealed class CareerHandlers(
    ItemData items,
    FormulaData formulas,
    CombatStats combat,
    MarkData marks,
    WorldState world,
    ILogger<CareerHandlers> logger)
{
    /// <summary>Sends the career form to the player.</summary>
    /// <param name="session">The player's session.</param>
    public void OpenForm(GameSession session)
    {
        session.Send(CareerMessages.ShowForm());
        logger.LogInformation("[{Session}] {Name}: career form (STR {Str}, INT {Int})", session.Id, session.ActiveCharacter!.Name,
            session.ActiveCharacter.Strength, session.ActiveCharacter.Intelligence);
    }

    /// <summary>
    /// Handles the job choice in the career form (<c>79/1</c>): checks the rebirth rules and bag room, reborns the character, gives the manteau,
    /// clears the trial mark, and tells the player and the map.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: one byte, the chosen job id.</param>
    internal ValueTask OnChoose(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (InWorld(session) is not { } c || body.Length < 1)
            return ValueTask.CompletedTask;
        var job = body.Span[0];
        if (Career.RebirthRefusal(c, job) is { } why)
        {
            logger.LogInformation("[{Session}] {Name}: rebirth as job {Job} refused ({Why})", session.Id, c.Name, job, why);
            return ValueTask.CompletedTask;
        }
        var manteau = Career.RebirthManteau(job, c.Level);
        var hasManteau = items[manteau] is not null;
        if (hasManteau && Bag.FreeSlot(c, items, manteau) == 0)
        {
            session.Send(CareerMessages.Result(1));
            return ValueTask.CompletedTask;
        }

        var (oldLevel, oldTotal) = (c.Level, Career.TotalLevel(c));
        Career.Rebirth(c, job, formulas);
        if (hasManteau)
        {
            foreach (var (stack, added) in Bag.Add(c, items, manteau, 1))
                session.Send(ItemMessages.BagAdd(new BagEntry { Slot = stack.Slot, ItemId = manteau, Count = added, Wear = stack.Wear }));
        }
        if (c.Marks.RemoveAll(m => m.Id == Career.TrialMark) > 0)
            session.Send(MarkMessages.Delete(Career.TrialMark));
        var flagged = marks.TrySetFlag(session, c, Career.TrialFlag, on: true);
        SendProgress(session, c);
        SendJob(session, c);
        combat.Send(session);
        var levelUp = PlayerMessages.LevelUp(session.Account!.LoginId);
        session.Send(levelUp);
        world.Broadcast(session, levelUp, sameMapOnly: true);
        logger.LogInformation("[{Session}] {Name} is reborn as job {Job}: level {Old} -> {Level} (total {OldTotal} -> {Total}), manteau {Manteau}, flag {Flag} {State}",
            session.Id, c.Name, job, oldLevel, c.Level, oldTotal, Career.TotalLevel(c), manteau, Career.TrialFlag, flagged ? "on" : "not set");
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Handles a job change (<c>79/2</c>): checks the rules and the items, uses up a Transfer Mirror, swaps the manteau and sends the new job.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: job id, bag slot of the Transfer Mirror, bag slot of the class manteau, one byte each.</param>
    internal ValueTask OnChange(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (InWorld(session) is not { } c || body.Length < 3)
            return ValueTask.CompletedTask;
        var (job, mirrorSlot, cloakSlot) = (body.Span[0], body.Span[1], body.Span[2]);
        var mirror = c.Bag.Find(b => b.Slot == mirrorSlot);
        var cloak = c.Bag.Find(b => b.Slot == cloakSlot);
        var why = Career.ChangeRefusal(c, job)
            ?? (mirror?.ItemId != Career.TransferMirror ? "no Transfer Mirror in that slot"
                : cloak is null || items[cloak.ItemId]?.SubType != Career.ManteauSubType ? "no class manteau in that slot"
                    : null);
        if (why is not null)
        {
            logger.LogInformation("[{Session}] {Name}: job change to {Job} refused ({Why})", session.Id, c.Name, job, why);
            return ValueTask.CompletedTask;
        }

        mirror!.Count--;
        if (mirror.Count == 0)
            c.Bag.Remove(mirror);
        session.Send(ItemMessages.BagSlotDecrease(mirrorSlot, 1));
        var newManteau = Career.SwapManteau(cloak!.ItemId, job);
        var oldManteau = cloak.ItemId;
        if (items[newManteau] is not null)
        {
            session.Send(ItemMessages.BagSlotDecrease(cloakSlot, 1));
            cloak.ItemId = newManteau;
            session.Send(ItemMessages.BagAdd(new BagEntry { Slot = cloak.Slot, ItemId = newManteau, Count = cloak.Count, Wear = cloak.Wear }));
        }
        var oldJob = c.Job;
        c.Job = job;
        SendJob(session, c);
        combat.Send(session);
        logger.LogInformation("[{Session}] {Name} changes job {Old} -> {Job}; manteau {OldManteau} -> {Manteau}", session.Id, c.Name,
            oldJob, job, oldManteau, cloak.ItemId);
        return ValueTask.CompletedTask;
    }

    /// <summary>Sends the player their reborn count, job, exp, level, max and current HP and SP.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="c">The player's character.</param>
    private static void SendProgress(GameSession session, Character c)
    {
        session.Send(InnMessages.SetAttribute(Action8.AttrReborn, c.RebornCount));
        session.Send(InnMessages.SetAttribute(Action8.AttrJob, c.Job));
        session.Send(InnMessages.SetAttribute(Action8.AttrExp, c.Exp));
        session.Send(InnMessages.SetAttribute(Action8.AttrLevel, c.Level));
        session.Send(InnMessages.SetAttribute(Action8.AttrMaxHp, c.MaxHp));
        session.Send(InnMessages.SetAttribute(Action8.AttrMaxSp, c.MaxSp));
        session.Send(InnMessages.SetAttribute(Action8.AttrHp, c.Hp));
        session.Send(InnMessages.SetAttribute(Action8.AttrSp, c.Sp));
    }

    /// <summary>Sends the character's job to the player and to every player in the world.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="c">The player's character.</param>
    private void SendJob(GameSession session, Character c)
    {
        var packet = CareerMessages.PlayerJob(session.Account!.LoginId, c.Job);
        session.Send(packet);
        world.Broadcast(session, packet, sameMapOnly: false);
    }

    /// <summary>Gives the active character of a session that is in the world.</summary>
    /// <param name="session">The player's session.</param>
    /// <returns>The character, or null when the session is not in the world.</returns>
    private static Character? InWorld(GameSession session)
    {
        var character = session.ActiveCharacter;
        return session.State == SessionState.InWorld && character is not null && session.Account is not null ? character : null;
    }
}
