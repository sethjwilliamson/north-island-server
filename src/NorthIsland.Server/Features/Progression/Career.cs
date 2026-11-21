using NorthIsland.GameData.Formulas;

namespace NorthIsland.Server.Features.Progression;

/// <summary>The six jobs of the career system and the rules for choosing (rebirth) and changing one, plus the class manteau items tied to each job.</summary>
public static class Career
{
    /// <summary>Job id 1, the first of the three fighter jobs (ids 1 to 3).</summary>
    public const byte Killer = 1;

    /// <summary>Job id 2, a fighter job.</summary>
    public const byte Warrior = 2;

    /// <summary>Job id 3, the last of the fighter jobs.</summary>
    public const byte Knight = 3;

    /// <summary>Job id 4, the first of the three caster jobs (ids 4 to 6).</summary>
    public const byte Wit = 4;

    /// <summary>Job id 5, a caster job.</summary>
    public const byte Priest = 5;

    /// <summary>Job id 6, the last of the caster jobs.</summary>
    public const byte Seer = 6;

    /// <summary>Event service number that opens the career form; the event engine calls <see cref="CareerHandlers.OpenForm"/> for it.</summary>
    public const int ServiceCode = 10;

    /// <summary>Lowest level at which a character can be reborn.</summary>
    public const byte RequiredLevel = 100;

    /// <summary>Id of the quest mark that records the rebirth trial; it is removed from the character on rebirth.</summary>
    public const ushort TrialMark = 15090; // First Trial

    /// <summary>
    /// Count of the trial mark once the rebirth trial is done: the trial NPCs open the career form only at this count (events 3 and 4 of map
    /// 12625), and a rebirth needs at least this count.
    /// </summary>
    public const byte TrialDone = 12;

    /// <summary>Id of the mark flag set on a character once the rebirth trial is done.</summary>
    public const ushort TrialFlag = 15091; // First Trial

    /// <summary>Item id of the Transfer Mirror, which a job change uses up.</summary>
    public const ushort TransferMirror = 34018; // Transfer Mirror

    /// <summary>Item sub type shared by all class manteaus.</summary>
    public const byte ManteauSubType = 142;

    /// <summary>Item id of the first class manteau: the level 1 manteau of job 1.</summary>
    public const ushort FirstManteauId = 25231; // Killer Manteau

    /// <summary>Number of manteau items for each job.</summary>
    public const int ManteausPerJob = 50;

    /// <summary>Tells whether a job id is one of the six jobs.</summary>
    /// <param name="job">The job id to test.</param>
    /// <returns>True for ids 1 to 6.</returns>
    public static bool IsJob(byte job) => job is >= Killer and <= Seer;

    /// <summary>Finds the first (level 1) manteau of a job.</summary>
    /// <param name="job">A job id from 1 to 6.</param>
    /// <returns>The item id of the job's first manteau.</returns>
    public static ushort FirstManteau(byte job) => (ushort)(FirstManteauId + (job - 1) * ManteausPerJob);

    /// <summary>
    /// Picks the manteau a character gets on rebirth: one step up the job's list for every two levels above 100, capped at the last of the 50.
    /// </summary>
    /// <param name="job">The job being chosen.</param>
    /// <param name="level">The character's level at rebirth.</param>
    /// <returns>The item id of the manteau.</returns>
    public static ushort RebirthManteau(byte job, int level) =>
        (ushort)(FirstManteau(job) + Math.Clamp((level - 100) / 2, 0, ManteausPerJob - 1));

    /// <summary>Finds which job a class manteau belongs to.</summary>
    /// <param name="itemId">Any item id.</param>
    /// <returns>The job id from 1 to 6, or 0 when the item is not a class manteau.</returns>
    public static byte JobOfManteau(ushort itemId) =>
        itemId is >= FirstManteauId and < FirstManteauId + 6 * ManteausPerJob ? (byte)((itemId - FirstManteauId) / ManteausPerJob + 1) : (byte)0;

    /// <summary>
    /// Tells whether the career form offers a job to a character: fighter jobs when strength is higher than intelligence, caster jobs when it is
    /// lower, any job when equal.
    /// </summary>
    /// <param name="c">The character.</param>
    /// <param name="job">The job id.</param>
    /// <returns>True when the job may be chosen.</returns>
    public static bool Offers(Character c, byte job) =>
        IsJob(job) && (c.Strength == c.Intelligence || (job <= Knight) == (c.Strength > c.Intelligence));

    /// <summary>Checks the rules for a rebirth into a job (not reborn yet, level, no unspent stat points, job offered).</summary>
    /// <param name="c">The character.</param>
    /// <param name="job">The chosen job id.</param>
    /// <returns>The reason for the refusal as text, or null when the rebirth is allowed.</returns>
    public static string? RebirthRefusal(Character c, byte job) =>
        c.RebornCount != 0 ? "already reborn"
            : c.Level < RequiredLevel ? $"level {c.Level} < {RequiredLevel}"
                : c.StatPoints != 0 ? "stat points not distributed"
                    : !Offers(c, job) ? $"job {job} not offered (STR {c.Strength}, INT {c.Intelligence})"
                        : null;

    /// <summary>
    /// Applies a rebirth: reborn count 1, the new job, level becomes level mod 100 + 1 with the matching exp, stats recalculated, HP and SP refilled.
    /// </summary>
    /// <param name="c">The character to change.</param>
    /// <param name="job">The chosen job id.</param>
    /// <param name="formulas">The formula data for the exp table and the stat recalculation.</param>
    public static void Rebirth(Character c, byte job, FormulaData formulas)
    {
        c.RebornCount = 1;
        c.Job = job;
        c.Level = (byte)(c.Level % 100 + 1);
        c.Exp = formulas.Table is { } f ? f.TotalLevelExp(c.Level, c.RebornCount) : 0;
        formulas.Recalculate(c);
        c.Hp = c.MaxHp;
        c.Sp = c.MaxSp;
    }

    /// <summary>
    /// Checks the rules for a reborn character changing to another job (reborn once, has a job, a different job, no unspent stat points, job
    /// offered).
    /// </summary>
    /// <param name="c">The character.</param>
    /// <param name="job">The wanted job id.</param>
    /// <returns>The reason for the refusal as text, or null when the change is allowed.</returns>
    public static string? ChangeRefusal(Character c, byte job) =>
        c.RebornCount != 1 ? "not reborn"
            : !IsJob(c.Job) ? "no job"
                : job == c.Job ? "same job"
                    : c.StatPoints != 0 ? "stat points not distributed"
                        : !Offers(c, job) ? $"job {job} not offered (STR {c.Strength}, INT {c.Intelligence})"
                            : null;

    /// <summary>Maps a class manteau to the same position in another job's list; a non-manteau item becomes the new job's first manteau.</summary>
    /// <param name="manteau">The item id of the manteau being swapped.</param>
    /// <param name="newJob">The job being changed to.</param>
    /// <returns>The item id of the new manteau.</returns>
    public static ushort SwapManteau(ushort manteau, byte newJob) =>
        JobOfManteau(manteau) is var job and not 0
            ? (ushort)(FirstManteau(newJob) + (manteau - FirstManteau(job)))
            : FirstManteau(newJob);

    /// <summary>Computes the total level over all rebirths.</summary>
    /// <param name="c">The character.</param>
    /// <returns>The total level from the reborn count and level.</returns>
    public static int TotalLevel(Character c) => FormulaTable.TotalLevel(c.Level, c.RebornCount);
}
