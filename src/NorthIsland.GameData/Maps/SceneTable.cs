using System.Buffers.Binary;
using System.Text;

namespace NorthIsland.GameData.Maps;

/// <summary>The scenes of SceneData.Dat, looked up by id, and the instance scenes among them.</summary>
public sealed class SceneTable
{
    /// <summary>Size of one SceneData.Dat record in bytes.</summary>
    public const int RecordSize = 131;

    /// <summary>Largest number of instance scenes taken from the file: 25, as the 2014 client (AddDupMission stops above 25; the
    /// 2008 and 2010 clients stopped at 19). The 2014 data has 20, the 20th being Sealing Space (30025, quest 702).</summary>
    public const int MaxInstances = 25;

    /// <summary>The scenes by id.</summary>
    private readonly Dictionary<ushort, SceneRecord> _byId;

    /// <summary>Creates the table.</summary>
    /// <param name="byId">The scenes by id.</param>
    /// <param name="instances">The instance scenes in list order.</param>
    private SceneTable(
        Dictionary<ushort, SceneRecord> byId,
        List<InstanceScene> instances)
    {
        _byId = byId;
        Instances = instances;
    }

    /// <summary>Looks up a scene; null when the id is unknown.</summary>
    /// <param name="id">The scene id.</param>
    public SceneRecord? this[int id] => id is >= 0 and <= ushort.MaxValue ? _byId.GetValueOrDefault((ushort)id) : null;

    /// <summary>Number of scenes.</summary>
    public int Count => _byId.Count;

    /// <summary>All scenes.</summary>
    public IEnumerable<SceneRecord> Scenes => _byId.Values;

    /// <summary>The instance scenes in list order.</summary>
    public IReadOnlyList<InstanceScene> Instances { get; }

    /// <summary>Finds the instance that a map is a copy of.</summary>
    /// <param name="mapId">The map id.</param>
    /// <returns>The instance, or null when the map is not an instance map.</returns>
    public InstanceScene? InstanceOfMap(int mapId)
    {
        var number = (mapId - InstanceScene.FirstMapId) / InstanceScene.Copies + 1;
        return mapId >= InstanceScene.FirstMapId && number <= Instances.Count ? Instances[number - 1] : null;
    }

    /// <summary>Reads and parses a SceneData.Dat file.</summary>
    /// <param name="path">Path of the file.</param>
    /// <returns>The parsed table.</returns>
    public static SceneTable Open(string path) => Parse(File.ReadAllBytes(path));

    /// <summary>
    /// Parses the contents of a SceneData.Dat file; throws <see cref="InvalidDataException"/> when it is too short or has the
    /// wrong version.
    /// </summary>
    /// <param name="data">The raw file contents.</param>
    /// <returns>The parsed table.</returns>
    public static SceneTable Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < RecordSize || BinaryPrimitives.ReadUInt16LittleEndian(data) != 4)
            throw new InvalidDataException("SceneData.Dat: version inconsistent");
        var byId = new Dictionary<ushort, SceneRecord>();
        List<InstanceScene> instances = [];
        for (var at = RecordSize; at + RecordSize <= data.Length; at += RecordSize)
        {
            var r = data.Slice(at, RecordSize);
            var id = W(r);
            if (id == 0)
                continue;
            var name = Name(r[2..23]);
            byId.TryAdd(id, new SceneRecord(id, name, B(r[23]), B(r[33]))); // 23 Type, 33 Flags
            var players = B(r[96]);
            if (players != 0 && id != 30005 && instances.Count < MaxInstances)
                instances.Add(new InstanceScene(instances.Count + 1, id, name, players,
                    B(r[97]) == 1, // 97 GuildOnly
                    W(r[101..]), // 101 DoneMark
                    W(r[103..]), // 103 MinLevel
                    W(r[105..]), // 105 Minutes
                    W(r[107..]), // 107 EntryX
                    W(r[109..]))); // 109 EntryY
        }
        return new SceneTable(byId, instances);
    }

    /// <summary>Decodes an obfuscated byte.</summary>
    /// <param name="v">The stored byte.</param>
    /// <returns>The decoded byte.</returns>
    private static byte B(byte v) => unchecked((byte)((v ^ 44) - 9));

    /// <summary>Decodes an obfuscated little-endian word.</summary>
    /// <param name="r">The bytes starting at the word.</param>
    /// <returns>The decoded word.</returns>
    private static ushort W(ReadOnlySpan<byte> r) => unchecked((ushort)((BinaryPrimitives.ReadUInt16LittleEndian(r) ^ 60012) - 9));

    /// <summary>Reads a scene name stored as a length byte followed by its characters in reverse order.</summary>
    /// <param name="s">The bytes starting at the length byte.</param>
    /// <returns>The trimmed name.</returns>
    private static string Name(ReadOnlySpan<byte> s)
    {
        var length = Math.Min((int)s[0], 20);
        var chars = new byte[20];
        for (var i = 0; i < 20; i++)
            chars[i] = s[20 - i];
        return Encoding.Latin1.GetString(chars, 0, length).Trim();
    }
}
