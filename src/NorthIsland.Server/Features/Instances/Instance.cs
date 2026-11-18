using NorthIsland.GameData.Maps;

namespace NorthIsland.Server.Features.Instances;

/// <summary>
/// One running or waiting instance (duplicate mission): its map copy, its members, its state and its goal progress.
/// </summary>
/// <param name="scene">The scene definition of the instance.</param>
/// <param name="copy">The index of the map copy, 0-based.</param>
/// <param name="creatorId">The login id of the player who created the instance.</param>
/// <param name="creatorName">The name of the character who created the instance.</param>
/// <param name="message">The message the creator gave, shown in the list.</param>
/// <param name="guild">The name of the guild the instance is restricted to; empty when it is open to everybody.</param>
/// <param name="createdUtc">The time the instance was created.</param>
public sealed class Instance(
    InstanceScene scene,
    int copy,
    int creatorId,
    string creatorName,
    string message,
    string guild,
    DateTime createdUtc)
{
    /// <summary>The size of the goal array; goal indexes run from 1 to 15.</summary>
    public const int GoalCount = 16;

    /// <summary>The event condition index that reads the asking player's own won battles instead of a goal.</summary>
    public const int PlayerWinsCondition = 9;

    /// <summary>How long a finished or timed-out instance stays open before it closes.</summary>
    public static readonly TimeSpan ClosingTime = TimeSpan.FromMinutes(10);

    /// <summary>Guards the members, state and goals of the instance; take it before reading or changing them.</summary>
    public Lock Lock { get; } = new();

    /// <summary>The scene definition of the instance.</summary>
    public InstanceScene Scene { get; } = scene;

    /// <summary>The runtime map id of this instance's map copy.</summary>
    public ushort MapId { get; } = scene.MapId(copy);

    /// <summary>The login id of the player who created the instance.</summary>
    public int CreatorId { get; } = creatorId;

    /// <summary>The name of the character who created the instance.</summary>
    public string CreatorName { get; } = creatorName;

    /// <summary>The message the creator gave, shown in the list.</summary>
    public string Message { get; } = message;

    /// <summary>The name of the guild the instance is restricted to; empty when it is open to everybody.</summary>
    public string Guild { get; } = guild;

    /// <summary>The time the instance was created.</summary>
    public DateTime CreatedUtc { get; } = createdUtc;

    /// <summary>The players in the instance, the creator first.</summary>
    public List<InstanceMember> Members { get; } = [];

    /// <summary>Whether the instance is still gathering members or has started.</summary>
    public InstanceState State { get; set; } = InstanceState.Waiting;

    /// <summary>The time the instance started; null while it is waiting.</summary>
    public DateTime? StartedUtc { get; set; }

    /// <summary>True once the instance goal (goal 1) has been reached.</summary>
    public bool Completed { get; set; }

    /// <summary>True once the time limit has run out before the goal was reached.</summary>
    public bool Expired { get; set; }

    /// <summary>The time the instance closes after it was completed or expired; null until then.</summary>
    public DateTime? ClosesUtc { get; set; }

    /// <summary>True once the players were warned that the instance is about to close.</summary>
    public bool WarnedClosing { get; set; }

    /// <summary>Since when a waiting instance has had no member online; null while a member is online.</summary>
    public DateTime? UnattendedSinceUtc { get; set; }

    /// <summary>The progress of each goal, indexed by goal.</summary>
    public int[] Goals { get; } = new int[GoalCount];

    /// <summary>The goals the events defined: the goal index to the npc id to beat and the number of times needed.</summary>
    public Dictionary<int, (ushort NpcId, int Needed)> Definitions { get; } = [];

    /// <summary>The npcs the whole instance sees hidden or shown, by role; true means hidden.</summary>
    public Dictionary<ushort, bool> NpcHidden { get; } = [];

    /// <summary>Finds a member by login id.</summary>
    /// <param name="playerId">The player's login id.</param>
    /// <returns>The member; null when the player is not in the instance.</returns>
    public InstanceMember? Member(int playerId) => Members.Find(m => m.Id == playerId);

    /// <summary>The time the time limit ends; null while the instance is waiting or its scene has no time limit.</summary>
    public DateTime? EndsUtc => StartedUtc is { } s && Scene.Minutes > 0 ? s.AddMinutes(Scene.Minutes) : null;

    /// <summary>
    /// The whole minutes left, rounded up, as the client's instance window shows them (85/9 is read as minutes and counted down one a minute,
    /// MZ_Menu.pas RecvDupRemainTime): until the instance closes once it is completed or expired, otherwise until the time limit ends.
    /// </summary>
    /// <param name="now">The current time.</param>
    /// <returns>The minutes left; null while the instance is waiting or its scene has no time limit.</returns>
    public int? MinutesLeft(DateTime now) => (ClosesUtc ?? EndsUtc) is { } end ? (int)Math.Ceiling(Math.Max(0, (end - now).TotalMinutes)) : null;

    /// <summary>Returns the progress of a goal.</summary>
    /// <param name="goal">The goal index.</param>
    /// <returns>The progress; 0 for an index outside the goals.</returns>
    public int Goal(int goal) => goal is >= 1 and < GoalCount ? Goals[goal] : 0;

    /// <summary>Adds to the progress of a goal; the result stays between 0 and 255, and goal 1 between 0 and 1.</summary>
    /// <param name="goal">The goal index.</param>
    /// <param name="value">The amount to add.</param>
    /// <returns>The new progress; 0 for an index outside the goals.</returns>
    public int AddGoal(int goal, int value)
    {
        if (goal is < 1 or >= GoalCount)
            return 0;
        return Goals[goal] = Math.Clamp(Goals[goal] + value, 0, goal == 1 ? 1 : byte.MaxValue);
    }

    /// <summary>Maps the condition index of an event script to a goal index: indexes above 9 read the goal one lower.</summary>
    /// <param name="index">The condition index.</param>
    /// <returns>The goal index.</returns>
    public static int GoalOfCondition(int index) => index > PlayerWinsCondition ? index - 1 : index;

    /// <summary>Returns the value an event condition compares: a player's won battles for index 9, otherwise a goal's progress.</summary>
    /// <param name="index">The condition index.</param>
    /// <param name="playerId">The login id of the player who asks.</param>
    /// <returns>The value.</returns>
    public int ConditionValue(int index, int playerId) =>
        index == PlayerWinsCondition ? Member(playerId)?.Wins ?? 0 : Goal(GoalOfCondition(index));

    /// <summary>Returns the slot of the client's instance window that shows a goal: goals 3 to 13 fill slots 1 to 11.</summary>
    /// <param name="goal">The goal index.</param>
    /// <returns>The window slot; 0 when the goal has none.</returns>
    public static byte ClientSlot(int goal) => goal is >= 3 and <= 13 ? (byte)(goal - 2) : (byte)0;
}
