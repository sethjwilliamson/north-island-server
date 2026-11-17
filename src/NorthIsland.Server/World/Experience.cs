using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NorthIsland.Protocol.Messages.Player;
using NorthIsland.Protocol.Opcodes;

namespace NorthIsland.Server.World;

/// <summary>Adds experience to characters and pets and applies the level-ups that follow.</summary>
/// <param name="combat">Recalculates and sends the character's values after a level-up.</param>
/// <param name="world">The online world, to announce level-ups to the map.</param>
/// <param name="formulas">The Formula.dat tables, for levels and stats.</param>
/// <param name="skills">The skill data, for the skills learned at a level.</param>
/// <param name="options">The pet options.</param>
/// <param name="itemOptions">The item options, for the amity ceiling.</param>
/// <param name="progressionOptions">The stat and skill points granted per level.</param>
/// <param name="logger">The logger.</param>
public sealed class Experience(
    CombatStats combat,
    WorldState world,
    FormulaData formulas,
    SkillData skills,
    IOptions<PetOptions> options,
    IOptions<ItemOptions> itemOptions,
    IOptions<ProgressionOptions> progressionOptions,
    ILogger<Experience> logger)
{
    /// <summary>Applies the level-ups a pet earned from its raised experience: raises its stats, sends them and adds amity for each level.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="pet">The pet, whose experience is already raised.</param>
    /// <param name="exp">The experience just gained, for the log.</param>
    /// <returns>True when the pet gained at least one level.</returns>
    public bool PetGains(GameSession session, Pet pet, int exp)
    {
        if (formulas.Table is not { } f)
            return false;
        var level = (byte)f.LevelFromExp(pet.Exp, pet.Reborn);
        var levelBefore = pet.Level;
        if (!Pets.LevelUp(pet, level, f, options.Value.PetStatsPerLevel, GameRandom.Shared.Next))
        {
            logger.LogInformation("[{Session}] pet {Pet} gains {Exp} exp ({Total} total)", session.Id, pet.Name, exp, pet.Exp);
            return false;
        }
        combat.RecalculatePet(pet, fill: true);
        foreach (var (attr, value) in new (byte, int)[]
        {
            (Action8.AttrStr, pet.Strength), (Action8.AttrCon, pet.Constitution), (Action8.AttrInt, pet.Intelligence),
            (Action8.AttrWis, pet.Wisdom), (Action8.AttrAgi, pet.Agility), (Action8.AttrLevel, pet.Level),
            (Action8.AttrMaxHp, pet.MaxHp), (Action8.AttrMaxSp, pet.MaxSp), (Action8.AttrHp, pet.Hp), (Action8.AttrSp, pet.Sp),
        })
            session.Send(PetMessages.SetAttribute(pet.Slot, attr, value));
        combat.SendPetValues(session, pet);
        var amity = (byte)Math.Min(
            Math.Max(pet.Amity, itemOptions.Value.ItemUse.MaxAmity),
            pet.Amity + (pet.Level - levelBefore) * options.Value.PetAmityPerLevel);
        if (amity != pet.Amity)
        {
            pet.Amity = amity;
            session.Send(PetMessages.SetAttribute(pet.Slot, Action8.AttrAmity, pet.Amity));
        }
        logger.LogInformation(
            "[{Session}] pet {Pet} gains {Exp} exp and reaches level {Level}: STR {Str} CON {Con} INT {Int} WIS {Wis} AGI {Agi}, HP {Hp}, SP {Sp}",
            session.Id, pet.Name, exp, pet.Level, pet.Strength, pet.Constitution, pet.Intelligence, pet.Wisdom, pet.Agility, pet.MaxHp, pet.MaxSp);
        return true;
    }

    /// <summary>Called with the session and the amount whenever a character earns experience; other features add their handler.</summary>
    public Action<GameSession, int>? Earned { get; set; }

    /// <summary>
    /// Reports experience that other code already credited to the character to the Earned handlers, without changing the
    /// character.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="exp">The experience earned.</param>
    public void Notify(GameSession session, int exp) => Earned?.Invoke(session, exp);

    /// <summary>
    /// Adds experience to the active character (kept within the int range), sends it, reports it to the Earned handlers and
    /// applies level-ups.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="exp">The experience to add.</param>
    public void Gain(GameSession session, int exp)
    {
        Earned?.Invoke(session, exp);
        var character = session.ActiveCharacter!;
        character.Exp = (int)Math.Clamp((long)character.Exp + exp, 0, int.MaxValue);
        session.Send(InnMessages.SetAttribute(Action8.AttrExp, character.Exp));
        ApplyLevelUps(session, announceToSelf: true);
    }

    /// <summary>
    /// Recalculates the character's level and, when it rose, grants stat and skill points, refills HP and SP, sends the new
    /// values and teaches the skills of the new levels.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="announceToSelf">Whether the level-up packet is also sent to the player; the others on the map always get it.</param>
    public void ApplyLevelUps(GameSession session, bool announceToSelf)
    {
        var character = session.ActiveCharacter!;
        var levelBefore = character.Level;
        combat.Recalculate(character);
        if (character.Level <= levelBefore)
            return;
        var levels = character.Level - levelBefore;
        character.StatPoints = (ushort)Math.Min(ushort.MaxValue, character.StatPoints + levels * progressionOptions.Value.StatPointsPerLevel);
        character.SkillPoints = (ushort)Math.Min(ushort.MaxValue, character.SkillPoints + levels * progressionOptions.Value.SkillPointsPerLevel);
        character.Hp = character.MaxHp;
        character.Sp = character.MaxSp;
        session.Send(InnMessages.SetAttribute(Action8.AttrLevel, character.Level));
        session.Send(InnMessages.SetAttribute(Action8.AttrMaxHp, character.MaxHp));
        session.Send(InnMessages.SetAttribute(Action8.AttrMaxSp, character.MaxSp));
        session.Send(InnMessages.SetAttribute(Action8.AttrHp, character.Hp));
        session.Send(InnMessages.SetAttribute(Action8.AttrSp, character.Sp));
        session.Send(InnMessages.SetAttribute(Action8.AttrStatPoints, character.StatPoints));
        session.Send(InnMessages.SetAttribute(Action8.AttrSkillPoints, character.SkillPoints));
        combat.Send(session);
        var levelUp = PlayerMessages.LevelUp(session.Account!.LoginId);
        world.Broadcast(session, levelUp, sameMapOnly: true);
        world.Broadcast(session, PlayerMessages.SceneLevel(session.Account!.LoginId, (byte)Math.Min(byte.MaxValue, character.Level)), sameMapOnly: true);
        if (announceToSelf)
            session.Send(levelUp);
        logger.LogInformation("[{Session}] {Name} reaches level {Level} (+{Stat} stat, +{Skill} skill points)", session.Id, character.Name,
            character.Level, levels * progressionOptions.Value.StatPointsPerLevel, levels * progressionOptions.Value.SkillPointsPerLevel);
        foreach (var skill in skills.LearnRoots(character, levelBefore))
        {
            session.Send(InnMessages.SetAttribute(Action8.AttrSkillLevel, 1, skill.Id));
            logger.LogInformation(
                "[{Session}] {Name} learns {Skill} ({Id}) at level {Level}",
                session.Id,
                character.Name,
                skill.Name,
                skill.Id,
                character.Level);
        }
    }
}
