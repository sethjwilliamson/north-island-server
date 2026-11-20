namespace NorthIsland.Server.Features.Events;

/// <summary>What the server does with a script result record.</summary>
public enum ResultAction
{
    /// <summary>Send the record to the client as it is.</summary>
    Client,

    /// <summary>Move the player through a convey (door) of the scene.</summary>
    Warp,

    /// <summary>Give the player items, or take them when the count is negative.</summary>
    Item,

    /// <summary>Add gold to the player, or take it when the amount is negative.</summary>
    Gold,

    /// <summary>Open a service window; the service code is in <see cref="MappedResult.Service"/>.</summary>
    Service,

    /// <summary>Start a minigame; its kind is in <see cref="MappedResult.Service"/>.</summary>
    Minigame,

    /// <summary>Start a battle of the scene; its index is in <see cref="MappedResult.Service"/>.</summary>
    Battle,

    /// <summary>Show an emotion over the player; its number (1..19, picture E1nn) is in <see cref="MappedResult.Service"/>.</summary>
    PlayerEmotion,

    /// <summary>Send the ready packet in <see cref="MappedResult.Packet"/>.</summary>
    Packet,

    /// <summary>Save the player's record point; its number is in <see cref="MappedResult.Service"/>.</summary>
    SavePoint,

    /// <summary>Add to, delete or set a quest mark of the player.</summary>
    Mark,

    /// <summary>Make a team NPC join or leave the player's team.</summary>
    TeamNpc,

    /// <summary>Change the amity of a team NPC: the NPC id is in <see cref="MappedResult.Service"/>, the change in <see cref="MappedResult.Convey"/>.</summary>
    Amity,

    /// <summary>Make an NPC play an action for everyone on the map.</summary>
    NpcAction,

    /// <summary>Give the player experience.</summary>
    Exp,

    /// <summary>Add progress to a goal of the instance.</summary>
    InstanceGoal,

    /// <summary>Define a goal of the instance: beat an NPC a number of times.</summary>
    InstanceGoalDefined,

    /// <summary>Run a step of the marriage: application, wedding or divorce.</summary>
    Marriage,

    /// <summary>Add a row to the point store or open it.</summary>
    PointStore,

    /// <summary>Charge a hire tier.</summary>
    Hire,

    /// <summary>Change the player's role.</summary>
    RoleChange,

    /// <summary>Roll the resource of a gathering spot; the kind is in <see cref="MappedResult.Service"/>, the tier in <see cref="MappedResult.Convey"/>.</summary>
    Gather,

    /// <summary>Teach the player the skill in <see cref="MappedResult.Service"/>.</summary>
    LearnSkill,

    /// <summary>Start the script cooldown in <see cref="MappedResult.Service"/> for the seconds in <see cref="MappedResult.Convey"/>.</summary>
    Cooldown,

    /// <summary>The record has no handler and is skipped.</summary>
    Unsupported,
}
