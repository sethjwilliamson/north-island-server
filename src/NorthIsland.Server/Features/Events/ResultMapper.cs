using NorthIsland.GameData.Events;
using NorthIsland.Protocol.Messages.Events;

namespace NorthIsland.Server.Features.Events;

/// <summary>Turns script result records into the actions the server takes.</summary>
public static class ResultMapper
{
    /// <summary>Maps a result record to its action and the data the action needs.</summary>
    /// <param name="r">The result record.</param>
    /// <returns>The mapped result; the action is <see cref="ResultAction.Unsupported"/> for a record the server does not handle.</returns>
    public static MappedResult Map(EveScriptRecord r)
    {
        switch (r.Kind)
        {
            case 1 when r.SubKind == 1 && r.Arg1 is 1 or 7: // 1/1/1 or 1/1/7: give or take item Arg2
                return new MappedResult(ResultAction.Item, new EventResult(0, 1, r.Arg2, 1, r.Value, 0),
                    Description: $"{(r.Value < 0 ? "take" : "give")} item {r.Arg2} x{Math.Abs(r.Value)}");
            case 1 when r.SubKind is 10 or 11 && r.Arg1 != 0 && r.Value >= 0: // 1/10 and 1/11: give a prize item
                return new MappedResult(ResultAction.Item, new EventResult(0, 1, r.Arg1, 1, Math.Max(1, r.Value), 0),
                    Description: $"give item {r.Arg1} x{Math.Max(1, r.Value)} (prize 1/{r.SubKind})");
            case 1 when r.SubKind == 1 && r.Arg1 == 2: // 1/1/2: change the gold by Value
                return new MappedResult(
                    ResultAction.Gold,
                    new EventResult(0, 5, 0, 0, r.Value, 0), // 5 Mode
                    Description: $"gold {(r.Value < 0 ? "-" : "+")}{Math.Abs(r.Value)}");
            case 1 when r.SubKind == 1 && r.Arg1 == 5: // 1/1/5: give experience
                return new MappedResult(ResultAction.Exp, Description: $"exp +{r.Value}", Service: r.Value);
            case 1 when r.SubKind == 1 && r.Arg1 == 6: // 1/1/6: the resource of a gathering spot of kind Arg2 and tier Value
                return new MappedResult(ResultAction.Gather, Description: $"gather kind {r.Arg2} tier {r.Value}", Service: r.Arg2, Convey: r.Value);
            case 11 when r.Arg1 == 1 && r.SubKind != 0: // 11/s/1: learn skill SubKind
                return new MappedResult(ResultAction.LearnSkill, Description: $"learn skill {r.SubKind}", Service: r.SubKind);
            case 14 when r.Arg1 == 1: // 14/s/1: start script cooldown SubKind for Value seconds
                return new MappedResult(ResultAction.Cooldown, Description: $"cooldown {r.SubKind} for {r.Value} s", Service: r.SubKind, Convey: r.Value);
            case 1 when r.SubKind == 7 && r.Arg1 is >= 1 and <= 255: // 1/7: the player shows emotion Arg1 (K9 M1 O1, the player twin of 2/r/8)
                return new MappedResult(ResultAction.PlayerEmotion, Description: $"player emotion {r.Arg1}", Service: r.Arg1);
            case 1 when r.SubKind == 8 && r.Arg1 == 2: // 1/8/2: charge a hire tier
                return new MappedResult(ResultAction.Hire, Description: $"charge tier {r.Value}", Service: r.Value);
            case 1 when r.SubKind == 2: // 1/2: the player says a line
                return Client(new EventResult(1, 7, (ushort)r.Value2, (byte)r.Arg2, 0, r.Arg1), $"player says {r.Arg1}"); // 7 Mode
            case 1 when r.SubKind == 3: // 1/3: warp through a convey point
                return new MappedResult(ResultAction.Warp, Convey: r.Arg1, Description: $"warp via convey {r.Arg1}");
            case 1 when r.SubKind == 4: // 1/4: show a dialog
                return Client(new EventResult(6, 7, 0, 0, 0, r.Arg1), $"dialog {r.Arg1}"); // 6 Kind, 7 Mode
            case 1 when r.SubKind == 5: // 1/5: save the return point
                return new MappedResult(ResultAction.SavePoint, Description: $"save record point {r.Arg1}", Service: r.Arg1);
            case 1 when r.SubKind == 6: // 1/6: turn the player
                return Client(new EventResult(14, (byte)r.Arg1, r.Arg2, 0, 0, 0), "player turns"); // 14 Kind
            case 2: // 2: a result about an NPC, chosen by Arg1
                return MapNpcResult(r, role: r.SubKind);
            case 5 when r.Arg1 is >= 1 and <= 3: // 5: add to, delete or set a quest mark
                return new MappedResult(ResultAction.Mark, Description: r.Arg1 == 3 && r.Value2 == 1
                    ? $"mark {r.SubKind} bit {r.Arg2} = {r.Value}"
                    : $"mark {r.SubKind} {MarkVerb(r.Arg1)} {r.Value}");
            case 4 when r.SubKind == 1: // 4/1: start battle Arg1
                return new MappedResult(ResultAction.Battle, new EventResult(3, 0, 0, 0, 0, r.Arg1), // 3 Kind
                    Description: $"battle {r.Arg1}", Service: r.Arg1);
            case 7 when r.SubKind == 25: // 7/25: marriage application
                return new MappedResult(ResultAction.Marriage, Description: "marriage application", Service: (int)MarriageStep.Apply);
            case 10 when r.SubKind == 8: // 10/8: wedding
                return new MappedResult(ResultAction.Marriage, Description: "wedding", Service: (int)MarriageStep.Wedding);
            case 10 when r.SubKind == 9: // 10/9: divorce
                return new MappedResult(ResultAction.Marriage, Description: "divorce", Service: (int)MarriageStep.Divorce);
            case 7 when r.SubKind == 13: // 7/13: a point store row, or open the store
                return new MappedResult(ResultAction.PointStore, new EventResult(0, 0, 0, (byte)(r.Value2 is >= 1 and <= 50 ? r.Value2 : 1), r.Value, 0),
                    Convey: r.Arg2, Description: r.Arg1 == 1 ? $"point store row {r.Arg2} for {r.Value}" : "open the point store", Service: r.Arg1);
            case 7 when r.SubKind != 13: // 7: open a service window
                return new MappedResult(ResultAction.Service, Description: $"service 7/{r.SubKind}", Service: r.SubKind);
            case 9: // 9: start a minigame
                return new MappedResult(ResultAction.Minigame, new EventResult(0, 0, 0, (byte)r.Arg2, r.Arg1, 0),
                    Description: $"minigame {r.SubKind} (value {r.Arg1}, level {r.Arg2})", Service: r.SubKind);
            case 8: // 8: play a movie
                return Client(new EventResult(5, 0, 0, (byte)r.SubKind, r.Value, 0), $"movie {r.Value}"); // 5 Kind
            case 3 when r.SubKind is 1 or 2: // 3/1 and 3/2: an NPC joins or leaves the team
                return new MappedResult(ResultAction.TeamNpc, Description: $"NPC {r.Arg1} {(r.SubKind == 1 ? "joins" : "leaves")} the team", Service: r.Arg1,
                    Convey: r.SubKind);
            case 3 when r.SubKind == 5 && r.Arg1 != 0: // 3/5: the amity of team NPC Arg1 changes by Value
                return new MappedResult(ResultAction.Amity, Description: $"NPC {r.Arg1} amity {r.Value:+0;-0}", Service: r.Arg1, Convey: r.Value);
            case 12 when r.SubKind == 1 && r.Arg1 is >= 1 and <= 255: // 12/1: change the role
                return new MappedResult(ResultAction.RoleChange, Description: $"role change to {r.Arg1}", Service: r.Arg1);
            case 13 when r.Arg2 == 0: // 13: minimap sign of -1, skipped
                return new MappedResult(ResultAction.Packet, Description: $"13/{r.SubKind} {r.Arg1} sign -1 (skipped)", Packet: []);
            case 13: // 13: set a minimap sign
                return new MappedResult(ResultAction.Packet, Description: $"map sign {r.SubKind}/{r.Arg1} = {r.Arg2 - 1}",
                    Packet: EventMessages.MapSign((byte)r.SubKind, r.Arg1, (byte)(r.Arg2 - 1)));
            case 17: // 17: add to an instance goal
                return new MappedResult(ResultAction.InstanceGoal, new EventResult(0, 0, 0, 0, r.Value, 0),
                    Description: $"instance goal {r.SubKind} +{r.Value}", Service: r.SubKind);
            case 18: // 18: define an instance goal
                return new MappedResult(ResultAction.InstanceGoalDefined, new EventResult(0, 0, 0, 0, r.Value, 0),
                    Description: $"instance goal {r.Arg1} = beat NPC {r.Arg2} x{r.Value}", Service: r.Arg1, Convey: r.Arg2);
            case 10 when r.SubKind == 6: // 10/6: instance reward bookkeeping, no effect
                return new MappedResult(ResultAction.Packet, Description: "10/6 (instance reward bookkeeping, no effect)", Packet: []);
            case 15 when r.SubKind == 1: // 15/1: play music
                return Client(new EventResult(15, (byte)r.Arg1, 0, 0, r.Value, 0), $"music {r.Value}"); // 15 Kind
            default: // any other record: unsupported
                return Unsupported(r);
        }
    }

    /// <summary>Maps a result record about an NPC: it speaks, hides, shows, changes state, emotes, acts, moves or turns.</summary>
    /// <param name="r">The result record.</param>
    /// <param name="role">The NPC role the record is about.</param>
    /// <returns>The mapped result.</returns>
    private static MappedResult MapNpcResult(EveScriptRecord r, ushort role) => r.Arg1 switch
    {
        1 => Client(new EventResult(1, 3, role, (byte)r.Value2, 0, r.Arg2), $"NPC {role} says {r.Arg2}"), // 3 Mode
        2 => Client(new EventResult(0, 3, role, 3, r.Value, r.Arg2), $"hide NPC {role}"), // 3 Mode, 3 Option
        3 => Client(new EventResult(0, 3, role, 4, r.Value, r.Arg2), $"show NPC {role}"), // 3 Mode, 4 Option
        5 => Client(new EventResult(0, 3, role, 5, r.Value, r.Arg2), $"NPC {role} state {r.Value}"), // 3 Mode, 5 Option
        6 => Client(new EventResult(6, 3, role, 0, 0, r.Arg2), $"dialog {r.Arg2} by NPC {role}"), // 6 Kind, 3 Mode
        8 => new MappedResult(ResultAction.Packet, Description: $"NPC {role} emotion {r.Arg2}",
            Packet: EventMessages.NpcEmotion(role, (byte)r.Arg2)), // K9 M3 O1: NPC emotion (values 2..16, pictures E102..E116)
        4 => new MappedResult(ResultAction.NpcAction, Description: $"NPC {role} action {r.Arg2}",
            Packet: EventMessages.NpcAction(role, (byte)r.Arg2), Service: role), // K9 M3 O2: NPC plays an action (values are action group firsts 8, 16, 26, 46...)
        7 => new MappedResult(ResultAction.Packet, Description: $"NPC {role} action {r.Arg2} facing {r.Value}",
            Packet: EventMessages.NpcActionDir(role, (byte)r.Arg2, (byte)r.Value)), // NPC plays an action facing a direction
        9 when r.Arg2 is >= 1 and <= 3 => Client(new EventResult(4, 3, role, (byte)r.Arg2, r.Value, 0), $"NPC {role} moves"), // 4 Kind, 3 Mode
        10 when r.Arg2 is >= 1 and <= 3 => Client(new EventResult(11, (byte)role, r.Arg1, (byte)r.Arg2, r.Value2, 0), $"NPC {role} turns"), // 11 Kind
        _ => Unsupported(r), // any other code: unsupported
    };

    /// <summary>Names the operation of a mark result for the log.</summary>
    /// <param name="op">The operation code: 1 add, 2 delete, anything else set.</param>
    /// <returns>The verb.</returns>
    private static string MarkVerb(int op) => op switch
    {
        1 => "add", // add to the mark
        2 => "delete", // delete the mark
        _ => "set" // any other code: set the mark
    };

    /// <summary>Builds a mapped result that sends a record to the client.</summary>
    /// <param name="wire">The record to send.</param>
    /// <param name="description">The text for the log.</param>
    /// <returns>The mapped result.</returns>
    private static MappedResult Client(EventResult wire, string description) => new(ResultAction.Client, wire, Description: description);

    /// <summary>Builds a mapped result for a record the server does not handle, describing the record for the log.</summary>
    /// <param name="r">The result record.</param>
    /// <returns>The mapped result.</returns>
    private static MappedResult Unsupported(EveScriptRecord r) =>
        new(ResultAction.Unsupported, Description: $"result {r.Kind}/{r.SubKind}/{r.Arg1} (arg2 {r.Arg2}, value {r.Value})");
}
