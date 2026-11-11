using System.Globalization;
using NorthIsland.GameData.Events;

namespace NorthIsland.Tools.EveScan;

/// <summary>Queries eve.Emg for the NPCs, dialogs, quest marks and items of the maps. See README.md.</summary>
internal static class Program
{
    private const string Usage = """
        usage: evescan [--data DIR | --eve FILE] [--map FILTER] COMMAND [ARGS]

          npcs                   list the NPCs: place index, NPC id, position, event ids
          dialogs [MIN]          NPCs whose events open a dialog with at least MIN choices (default 1)
          mark ID                events that add to, delete or set quest mark ID
          item ID                events that give or take item ID

          --data DIR             game data folder holding data/eve.Emg (default: $NI_GAME_DATA)
          --eve FILE             the eve.Emg file itself
          --map FILTER           maps to scan: 10037, 10000-10099 or 10037,12003 (default: all)
        """;

    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"evescan: {ex.Message}");
            Console.Error.WriteLine(Usage);
            return 2;
        }
    }

    private static int Run(string[] args)
    {
        string? data = Environment.GetEnvironmentVariable("NI_GAME_DATA"), eve = null, mapFilter = null;
        var rest = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--data": data = Next(args, ref i); break;
                case "--eve": eve = Next(args, ref i); break;
                case "--map": mapFilter = Next(args, ref i); break;
                case "-h" or "--help": Console.WriteLine(Usage); return 0;
                default: rest.Add(args[i]); break;
            }
        }
        if (rest.Count == 0)
            throw new ArgumentException("no command given");

        eve ??= data is null ? throw new ArgumentException("give --data or --eve") : FindEve(data);
        var archive = EveArchive.Open(eve);
        var maps = MapFilter.Parse(mapFilter);
        var scenes = archive.MapIds.Distinct().Where(maps.Contains).Select(id => (Map: id, Scene: Load(archive, id)))
            .Where(s => s.Scene is not null).Select(s => (s.Map, Scene: s.Scene!));

        switch (rest[0])
        {
            case "npcs":
                foreach (var (map, scene) in scenes)
                    ListNpcs(map, scene);
                return 0;
            case "dialogs":
                var min = rest.Count > 1 ? Number(rest[1]) : 1;
                foreach (var (map, scene) in scenes)
                    FindDialogs(map, scene, min);
                return 0;
            case "mark" when rest.Count > 1:
                var mark = Number(rest[1]);
                FindResults(scenes, r => r.Kind == 5 && r.Arg1 is >= 1 and <= 3 && r.SubKind == mark, DescribeMark);
                return 0;
            case "item" when rest.Count > 1:
                var item = Number(rest[1]);
                FindResults(scenes, r => ItemOf(r) == item, DescribeItem);
                return 0;
            default:
                throw new ArgumentException($"unknown command or missing argument: {string.Join(' ', rest)}");
        }
    }

    /// <summary>Prints every NPC of a map: its 1-based place index (the runtime role index), template id, position and events.</summary>
    private static void ListNpcs(int map, EveScene scene)
    {
        for (var i = 0; i < scene.Npcs.Count; i++)
        {
            var npc = scene.Npcs[i];
            Console.WriteLine(
                $"map {map} place {i + 1} npc {npc.NpcId} at ({npc.X},{npc.Y}) events [{string.Join(',', npc.EventIds)}] {npc.Name}");
        }
    }

    /// <summary>Prints the NPCs whose events open an interaction dialog (interact kind 1) with at least <paramref name="min"/> choices.</summary>
    private static void FindDialogs(int map, EveScene scene, int min)
    {
        for (var i = 0; i < scene.Npcs.Count; i++)
        {
            var npc = scene.Npcs[i];
            foreach (var ev in scene.EventsOf(npc))
            {
                foreach (var result in ev.Conditions.SelectMany(c => c.Results))
                {
                    // 1/4: the player's dialog Arg1; 2/x/6: a dialog by NPC SubKind, interact Arg2 (see ResultMapper)
                    int? index = result.Kind == 1 && result.SubKind == 4 ? result.Arg1
                        : result.Kind == 2 && result.Arg1 == 6 ? result.Arg2 : null;
                    if (index is null || scene.Interacts.FirstOrDefault(x => x.Index == index) is not { Kind: 1 } dialog)
                        continue;
                    var choices = dialog.Entries.Count(e => e.Kind == 3);
                    var answers = dialog.Entries.Count(e => e.Kind == 2);
                    if (choices < min)
                        continue;
                    Console.WriteLine(
                        $"map {map} place {i + 1} npc {npc.NpcId} event {ev.Index} dialog {index} choices {choices} answers {answers} {npc.Name}");
                }
            }
        }
    }

    /// <summary>Prints every event result that matches, with the map and the NPCs (if any) that run the event.</summary>
    private static void FindResults(
        IEnumerable<(int Map, EveScene Scene)> scenes,
        Func<EveScriptRecord, bool> match,
        Func<EveScriptRecord, string> describe)
    {
        foreach (var (map, scene) in scenes)
        {
            foreach (var ev in scene.Events.Concat(scene.LeadEvents))
            {
                foreach (var result in ev.Conditions.SelectMany(c => c.Results).Where(match))
                {
                    var npcs = scene.Npcs.Select((n, i) => (Npc: n, Place: i + 1))
                        .Where(x => x.Npc.EventIds.Contains((byte)ev.Index))
                        .Select(x => $"place {x.Place} npc {x.Npc.NpcId}").ToList();
                    var lead = scene.LeadEvents.Contains(ev) ? " (lead event)" : "";
                    Console.WriteLine(
                        $"map {map} event {ev.Index}{lead} {describe(result)} by {(npcs.Count > 0 ? string.Join("; ", npcs) : "no NPC")}");
                }
            }
        }
    }

    /// <summary>Returns the item a result gives or takes (1/1/1, 1/1/7, 1/10, 1/11), or null.</summary>
    private static int? ItemOf(EveScriptRecord r) => r.Kind switch
    {
        1 when r.SubKind == 1 && r.Arg1 is 1 or 7 => r.Arg2,
        1 when r.SubKind is 10 or 11 && r.Arg1 != 0 => r.Arg1,
        _ => null,
    };

    private static string DescribeItem(EveScriptRecord r) =>
        r.SubKind == 1
            ? $"{(r.Value < 0 ? "takes" : "gives")} item {r.Arg2} x{Math.Abs(r.Value)}"
            : $"gives item {r.Arg1} x{Math.Max(1, r.Value)} (prize 1/{r.SubKind})";

    private static string DescribeMark(EveScriptRecord r) => r.Arg1 switch
    {
        1 => $"adds {r.Value} to mark {r.SubKind}",
        2 => $"deletes mark {r.SubKind}",
        _ when r.Value2 == 1 => $"sets mark {r.SubKind} bit {r.Arg2} = {r.Value}",
        _ => $"sets mark {r.SubKind} = {r.Value}",
    };

    private static EveScene? Load(EveArchive archive, int map)
    {
        try
        {
            return archive.LoadScene(map);
        }
        catch (EveFormatException ex)
        {
            Console.Error.WriteLine($"evescan: map {map} could not be read: {ex.Message}");
            return null;
        }
    }

    private static string FindEve(string data)
    {
        foreach (var candidate in new[] { Path.Combine(data, "data", "eve.Emg"), Path.Combine(data, "eve.Emg") })
            if (File.Exists(candidate))
                return candidate;
        throw new ArgumentException($"no eve.Emg under {data}");
    }

    private static string Next(string[] args, ref int i) =>
        ++i < args.Length ? args[i] : throw new ArgumentException($"{args[i - 1]} needs a value");

    private static int Number(string s) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : throw new ArgumentException($"not a number: {s}");

    /// <summary>A set of map ids given as single ids and ranges separated by commas; null matches every map.</summary>
    private sealed class MapFilter
    {
        private readonly List<(int From, int To)>? _ranges;

        private MapFilter(List<(int From, int To)>? ranges) => _ranges = ranges;

        public static MapFilter Parse(string? filter)
        {
            if (string.IsNullOrWhiteSpace(filter))
                return new MapFilter(null);
            var ranges = new List<(int, int)>();
            foreach (var part in filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var dash = part.IndexOf('-', 1);
                ranges.Add(dash < 0 ? (Number(part), Number(part)) : (Number(part[..dash]), Number(part[(dash + 1)..])));
            }
            return new MapFilter(ranges);
        }

        public bool Contains(int map) => _ranges is null || _ranges.Exists(r => map >= r.From && map <= r.To);
    }
}
