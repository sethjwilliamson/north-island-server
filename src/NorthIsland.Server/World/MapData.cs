using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NorthIsland.GameData.Events;
using NorthIsland.GameData.Maps;

namespace NorthIsland.Server.World;

/// <summary>
/// The map data of the server: event scenes from eve.Emg, map zones from Ground.MMG and scenes from SceneData.Dat, each
/// loaded when first needed.
/// </summary>
public sealed class MapData
{
    /// <summary>The logger.</summary>
    private readonly ILogger<MapData> _logger;

    /// <summary>The eve.Emg event archive, loaded on first use; null when the file is missing.</summary>
    private readonly Lazy<EveArchive?> _archive;

    /// <summary>The event scenes read so far, by source map id.</summary>
    private readonly ConcurrentDictionary<int, EveScene?> _scenes = new();

    /// <summary>The ids of NPCs that have events, found on first use.</summary>
    private readonly Lazy<HashSet<ushort>> _scriptedNpcIds;

    /// <summary>The Ground.MMG map zone index, opened on first use; null when the file is missing.</summary>
    private readonly Lazy<MapZones?> _zoneIndex;

    /// <summary>The SceneData.Dat scene table, opened on first use; null when the file is missing.</summary>
    private readonly Lazy<SceneTable?> _sceneTable;

    /// <summary>The zones read so far, by source map id.</summary>
    private readonly ConcurrentDictionary<int, IReadOnlyList<MapZone>> _zones = new();

    /// <summary>The map sizes read so far, by source map id; null for a map the index does not hold (a house) or without the index.</summary>
    private readonly ConcurrentDictionary<int, (int Width, int Height)?> _mapSizes = new();

    /// <summary>For each map with a door into it, the first map holding such a door and that door, found on first use.</summary>
    private readonly Lazy<Dictionary<ushort, (ushort From, EveConvey Convey)>> _firstDoorInto;

    /// <summary>Sets up lazy loading of the map files in the game data directory.</summary>
    /// <param name="options">The server options, for the game data directory.</param>
    /// <param name="logger">The logger.</param>
    public MapData(
        IOptions<ServerOptions> options,
        ILogger<MapData> logger)
    {
        _logger = logger;
        var path = Path.Combine(ServerPaths.Resolve(options.Value.GameDataDirectory), "eve.Emg");
        _archive = new Lazy<EveArchive?>(() => Load(path));
        _scriptedNpcIds = new Lazy<HashSet<ushort>>(FindScriptedNpcIds);
        _firstDoorInto = new Lazy<Dictionary<ushort, (ushort From, EveConvey Convey)>>(FindFirstDoors);
        var ground = Path.Combine(ServerPaths.Resolve(options.Value.GameDataDirectory), "Ground.MMG");
        _zoneIndex = new Lazy<MapZones?>(() =>
        {
            if (!File.Exists(ground))
                return null;
            var zones = MapZones.Open(ground);
            logger.LogInformation("Loaded the map index of {Count} maps from {Path}", zones.MapCount, ground);
            return zones;
        });
        var sceneData = Path.Combine(ServerPaths.Resolve(options.Value.GameDataDirectory), "SceneData.Dat");
        _sceneTable = new Lazy<SceneTable?>(() =>
        {
            if (!File.Exists(sceneData))
                return null;
            var table = SceneTable.Open(sceneData);
            logger.LogInformation("Loaded {Count} scenes from {Path}", table.Count, sceneData);
            return table;
        });
    }

    /// <summary>The battle background used when nothing else is found for a map.</summary>
    public const byte DefaultBattleBackground = 81;

    /// <summary>The battle background for a fight at a spot of a map: the zone there, else the map's scene type, else the default.</summary>
    /// <param name="mapId">The map id.</param>
    /// <param name="x">The x position.</param>
    /// <param name="y">The y position.</param>
    /// <returns>The battle background id.</returns>
    public byte BattleBackground(int mapId, int x, int y)
    {
        mapId = SourceMapId(mapId);
        var zones = _zones.GetOrAdd(mapId, id =>
        {
            try
            {
                return _zoneIndex.Value?.For(id) ?? [];
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or IndexOutOfRangeException)
            {
                _logger.LogWarning(ex, "Map {Map} zones could not be read", id);
                return [];
            }
        });
        foreach (var z in zones)
        {
            if (z.Contains(x, y) && z.Id != 0)
                return z.Id;
        }
        return _sceneTable.Value?[mapId] is { } scene ? BackgroundForScene(scene) : DefaultBattleBackground;
    }

    /// <summary>
    /// The battle background for a player-versus-player fight: a town background (Holy towns differ) for a town scene, else
    /// as for a monster fight.
    /// </summary>
    /// <param name="mapId">The map id.</param>
    /// <param name="x">The x position.</param>
    /// <param name="y">The y position.</param>
    /// <returns>The battle background id.</returns>
    public byte PkBackground(int mapId, int x, int y)
    {
        if (_sceneTable.Value?[SourceMapId(mapId)] is not { Type: 2 } town)
            return BattleBackground(mapId, x, y);
        return town.Name.Contains("Holy", StringComparison.OrdinalIgnoreCase) ? (byte)131 : (byte)130;
    }

    /// <summary>The battle background for a scene type.</summary>
    /// <param name="scene">The scene record.</param>
    /// <returns>The battle background id.</returns>
    public static byte BackgroundForScene(SceneRecord scene) => scene.Type switch
    {
        2 => 80, // town scenes
        3 => 140, // scene type 3
        4 => scene.Name.Contains("Cellar", StringComparison.OrdinalIgnoreCase) ? (byte)120 : (byte)11, // cellar scenes use 120, other scenes of this type 11
        5 => scene.Name.Contains("Cabin", StringComparison.OrdinalIgnoreCase) || scene.Name.Contains("Walkway", StringComparison.OrdinalIgnoreCase)
            ? (byte)26
            : (byte)141, // other scenes of this type use 141
        6 => 110, // scene type 6
        8 => 141, // scene type 8
        9 => 15, // scene type 9
        10 => 50, // scene type 10
        11 or 14 => 27, // scene types 11 and 14
        12 => 70, // scene type 12
        _ => DefaultBattleBackground, // any other type: the default battle background
    };

    /// <summary>The name of a map's scene.</summary>
    /// <param name="mapId">The map id.</param>
    /// <returns>The name, empty when unknown.</returns>
    public string SceneName(int mapId) => _sceneTable.Value?[mapId]?.Name ?? "";

    /// <summary>The scene type of a map.</summary>
    /// <param name="mapId">The map id.</param>
    /// <returns>The scene type, 0 when unknown.</returns>
    public byte SceneType(int mapId) => _sceneTable.Value?[mapId]?.Type ?? 0;

    /// <summary>Finds the scene with a name, ignoring case and surrounding spaces and leaving battle backgrounds out.</summary>
    /// <param name="name">The scene name.</param>
    /// <returns>The lowest scene id with that name, or null when there is none.</returns>
    public ushort? SceneIdByName(string name) =>
        _sceneTable.Value?.Scenes.Where(s => s.Type != 7 && string.Equals(s.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(s => (ushort?)s.Id).Min();

    /// <summary>The first door found that leads into a map.</summary>
    /// <param name="mapId">The map id.</param>
    /// <returns>The map holding the door and a warp built from the door, or null when no door leads there.</returns>
    public (ushort From, DoorWarp Warp)? FirstDoorInto(ushort mapId) =>
        _firstDoorInto.Value.TryGetValue(mapId, out var d)
            ? (d.From, new DoorWarp(
                mapId,
                (ushort)Math.Clamp(d.Convey.X, 0, ushort.MaxValue),
                (ushort)Math.Clamp(d.Convey.Y, 0, ushort.MaxValue),
                (ushort)d.Convey.Index))
            : null;

    /// <summary>Scans the scenes of eve.Emg for doors to other maps and keeps the first one found for each target map.</summary>
    /// <returns>The first door by target map id.</returns>
    private Dictionary<ushort, (ushort From, EveConvey Convey)> FindFirstDoors()
    {
        Dictionary<ushort, (ushort, EveConvey)> first = [];
        if (_archive.Value is not { } archive)
            return first;
        foreach (var mapId in archive.MapIds)
        {
            try
            {
                foreach (var convey in archive.LoadScene(mapId).Conveys)
                    if (convey.TargetMapId != mapId)
                        first.TryAdd(convey.TargetMapId, ((ushort)mapId, convey));
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException)
            {
                _logger.LogWarning(ex, "Scene {Map} in eve.Emg could not be read", mapId);
            }
        }
        return first;
    }

    /// <summary>
    /// The width and height of a map from the map zone index, that of its source map for an instance copy; read from disk once for each map.
    /// </summary>
    /// <param name="mapId">The map id.</param>
    /// <returns>The size in pixels, or null when unknown (a house, or no map index).</returns>
    public (int Width, int Height)? MapSize(int mapId) => _mapSizes.GetOrAdd(SourceMapId(mapId), id => _zoneIndex.Value?.Size(id));

    /// <summary>The flags byte of a map's scene.</summary>
    /// <param name="mapId">The map id.</param>
    /// <returns>The flags, 0 when unknown.</returns>
    public byte SceneFlags(int mapId) => _sceneTable.Value?[mapId]?.Flags ?? 0;

    /// <summary>Whether the eve.Emg event archive was loaded.</summary>
    public bool Available => _archive.Value is not null;

    /// <summary>The instances listed in SceneData.Dat; empty without the file.</summary>
    public IReadOnlyList<InstanceScene> Instances => _sceneTable.Value?.Instances ?? [];

    /// <summary>Finds the instance a map id belongs to.</summary>
    /// <param name="mapId">The map id.</param>
    /// <returns>The instance, or null when the map is not an instance copy.</returns>
    public InstanceScene? InstanceOfMap(int mapId) => _sceneTable.Value?.InstanceOfMap(mapId);

    /// <summary>The map whose file and events a map id uses: the instance's scene for an instance copy, else the map itself.</summary>
    /// <param name="mapId">The map id.</param>
    /// <returns>The source map id.</returns>
    public int SourceMapId(int mapId) => InstanceOfMap(mapId)?.SceneId ?? mapId;

    /// <summary>The ids of the NPCs that have events or are the target of an event result in any scene.</summary>
    public IReadOnlySet<ushort> ScriptedNpcIds => _scriptedNpcIds.Value;

    /// <summary>Scans the scenes of eve.Emg for NPCs that have events or are targeted by event results.</summary>
    /// <returns>The NPC ids.</returns>
    private HashSet<ushort> FindScriptedNpcIds()
    {
        HashSet<ushort> ids = [];
        if (_archive.Value is not { } archive)
            return ids;
        foreach (var mapId in archive.MapIds)
        {
            try
            {
                var scene = archive.LoadScene(mapId);
                var targeted = scene.Events.Concat(scene.LeadEvents).SelectMany(e => e.Conditions).SelectMany(c => c.Results)
                    .Where(r => r.Kind == 2).Select(r => (int)r.SubKind).ToHashSet();
                for (var role = 1; role <= scene.Npcs.Count; role++)
                    if (scene.Npcs[role - 1].EventIds.Count > 0 || targeted.Contains(role))
                        ids.Add((ushort)scene.Npcs[role - 1].NpcId);
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException)
            {
                _logger.LogWarning(ex, "Scene {Map} in eve.Emg could not be read", mapId);
            }
        }
        return ids;
    }

    /// <summary>The event scene of a map, read once and kept; an instance copy gets the scene of its source map.</summary>
    /// <param name="mapId">The map id.</param>
    /// <returns>The scene, or null without eve.Emg, for an unknown map or when it cannot be read.</returns>
    public EveScene? Scene(int mapId) => _scenes.GetOrAdd(SourceMapId(mapId), id =>
    {
        var archive = _archive.Value;
        if (archive is null || !archive.Contains(id))
            return null;
        try
        {
            return archive.LoadScene(id);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException)
        {
            _logger.LogError(ex, "Scene {Map} in eve.Emg could not be read", id);
            return null;
        }
    });

    /// <summary>Opens the eve.Emg archive and logs that it is loaded.</summary>
    /// <param name="path">The path of eve.Emg.</param>
    /// <returns>The archive, or null when the file does not exist.</returns>
    private EveArchive? Load(string path)
    {
        if (!File.Exists(path))
        {
            _logger.LogWarning("Map event data not found at {Path}; doors and NPC events are disabled", path);
            return null;
        }
        var archive = EveArchive.Open(path);
        _logger.LogInformation("Loaded map event index from {Path}", path);
        return archive;
    }
}
