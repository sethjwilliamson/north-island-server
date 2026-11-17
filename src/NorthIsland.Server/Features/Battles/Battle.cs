using System.Collections.Immutable;
using NorthIsland.GameData.Events;
using NorthIsland.GameData.Items;
using NorthIsland.GameData.Npcs;
using NorthIsland.Protocol;
using NorthIsland.Protocol.Messages.Battle;

namespace NorthIsland.Server.Features.Battles;

/// <summary>One running battle: its players, pets and monsters, the round state and the commands chosen so far.</summary>
public sealed class Battle
{
    /// <summary>The row (4) a side 1 player stands in; their pet stands in <see cref="PetRow"/>.</summary>
    public const byte PlayerRow = 4;

    /// <summary>The column (2) the first player of a battle stands in.</summary>
    public const byte PlayerCol = 2;

    /// <summary>The row (3) a side 1 player's pet stands in.</summary>
    public const byte PetRow = 3;

    /// <summary>Item effect attribute ids of worn items that add to a fighter's attack, defence, speed, magic attack and magic defence.</summary>
    public const byte AttrAttack = 210, AttrDefence = 211, AttrSpeed = 214, AttrMagicAttack = 215, AttrMagicDefence = 216;

    /// <summary>The columns, besides <see cref="PlayerCol"/>, that further team members of a battle stand in.</summary>
    public static readonly ImmutableArray<byte> MemberCols = [1, 3, 4];

    /// <summary>The index of the battle in the event data that started it, 0 for a fight without one.</summary>
    public required int EventBattleIndex { get; init; }

    /// <summary>The map the battle started on.</summary>
    public ushort MapId { get; init; }

    /// <summary>The player fighters, in the order they joined.</summary>
    public List<Fighter> Players { get; } = [];

    /// <summary>The pet fighters of the players.</summary>
    public List<Fighter> Pets { get; } = [];

    /// <summary>The first player to join, who started the battle.</summary>
    public Fighter Player => Players[0];

    /// <summary>Finds the pet fighter of a player.</summary>
    /// <param name="player">The player fighter.</param>
    /// <returns>Their pet fighter, or null when they have none in the battle.</returns>
    public Fighter? PetOf(Fighter player) => Pets.Find(p => p.OwnerId == player.Id);

    /// <summary>Finds the player a fighter belongs to.</summary>
    /// <param name="f">A fighter; a pet is mapped to its owner.</param>
    /// <returns>The owner of a pet, otherwise <paramref name="f"/> itself.</returns>
    public Fighter OwnerOf(Fighter f) => f.Kind == 4 ? Players.Find(p => p.Id == f.OwnerId) ?? Player : f; // pet

    /// <summary>The monster fighters, including those that are down.</summary>
    public List<Fighter> Monsters { get; } = [];

    /// <summary>The fighters that fell during the current action and have not been knocked off the field yet.</summary>
    public List<Fighter> PendingKnockOffs { get; } = [];

    /// <summary>The number of the current round.</summary>
    public int Round { get; set; }

    /// <summary>The stage the current round is in.</summary>
    public BattlePhase Phase { get; set; } = BattlePhase.Command;

    /// <summary>The command each player or pet fighter has chosen for the current round.</summary>
    public Dictionary<Fighter, BattleCommand> Commands { get; } = [];

    /// <summary>The players and pets that have not left the battle.</summary>
    public IEnumerable<Fighter> OwnSide => Players.Concat(Pets).Where(f => !f.Left);

    /// <summary>The players that have not left the battle.</summary>
    public IEnumerable<Fighter> ActivePlayers => Players.Where(p => !p.Left);

    /// <summary>True while some fighter of the own side can still act this round and has not chosen a command.</summary>
    public bool AwaitsCommands => OwnSide.Any(f => f.IsAlive && !StatusRules.CannotAct(f) && !Commands.ContainsKey(f)
        && (f.Kind != 4 || OwnerOf(f).IsAlive) && f.JoinedRound != Round && f.SummonedRound != Round); // pet

    /// <summary>Finds the own side fighter standing in a cell.</summary>
    /// <param name="row">The row of the cell.</param>
    /// <param name="col">The column of the cell.</param>
    /// <returns>The player or pet in that cell, or null.</returns>
    public Fighter? OwnAt(byte row, byte col) => OwnSide.FirstOrDefault(f => f.Row == row && f.Col == col);

    /// <summary>True when no player still in the battle is alive.</summary>
    public bool AllPlayersDown => ActivePlayers.All(p => !p.IsAlive);

    /// <summary>True for a fight between players.</summary>
    public bool IsPvp { get; init; }

    /// <summary>True once a Delay Clock item has been used in this battle.</summary>
    public bool DelayClockUsed { get; set; }

    /// <summary>True for a fight against a single map NPC or monster that is not tied to a battle event.</summary>
    public bool Standalone { get; init; }

    /// <summary>The fight state value sent to the client: 2 for a fight between players or against a map NPC, otherwise 1.</summary>
    public byte FightState => IsPvp || Standalone ? (byte)2 : (byte)1;

    /// <summary>Tells whether every player of a side is down.</summary>
    /// <param name="side">The side number.</param>
    /// <returns>True when no player of that side still in the battle is alive.</returns>
    public bool SideDown(byte side) => ActivePlayers.Where(p => p.Side == side).All(p => !p.IsAlive);

    /// <summary>Lists the living fighters on the other side.</summary>
    /// <param name="f">The fighter whose enemies are wanted.</param>
    /// <returns>The living fighters whose side differs from <paramref name="f"/>.</returns>
    /// <remarks>
    /// In the order the client's auto play picks targets: on screen each row is a slanted line of cells, so the enemies come line by line,
    /// the left line first (rows 1 then 2, or 4 then 3 on the right side), and down each line by column.
    /// </remarks>
    public IEnumerable<Fighter> EnemiesOf(Fighter f) =>
        All.Where(x => x.Side != f.Side && x.IsAlive).OrderBy(x => x.Row <= 2 ? x.Row : -x.Row).ThenBy(x => x.Col);

    /// <summary>The background scene id sent to the client when the battle starts.</summary>
    public byte Background { get; set; }

    /// <summary>The battle type sent to the client, 1 for a fight against monsters.</summary>
    public byte Type { get; set; } = 1;

    /// <summary>The sessions of the players watching the battle.</summary>
    public List<GameSession> Watchers { get; } = [];

    /// <summary>Sends one packet to every player and watcher in the battle.</summary>
    /// <param name="packet">The packet to send.</param>
    /// <param name="except">A session that is skipped, or null.</param>
    public void Send(byte[] packet, GameSession? except = null) => Send(_ => packet, except);

    /// <summary>Sends a packet, built once per client generation, to every player and watcher in the battle.</summary>
    /// <param name="build">Builds the packet for a client generation.</param>
    /// <param name="except">A player session that is skipped, or null. Watchers always receive it.</param>
    public void Send(Func<ClientGeneration, byte[]> build, GameSession? except = null)
    {
        var built = new Dictionary<ClientGeneration, byte[]>();
        byte[] For(GameSession s)
        {
            if (!built.TryGetValue(s.Client, out var packet))
                built[s.Client] = packet = build(s.Client);
            return packet;
        }
        foreach (var p in ActivePlayers.Where(p => p.Session != except))
            p.Session?.Send(For(p.Session));
        lock (Watchers)
        {
            foreach (var w in Watchers)
                w.Send(For(w));
        }
    }

    /// <summary>Marks the session's player and pet fighters as having left the battle.</summary>
    /// <param name="session">The session that left.</param>
    public void Abandon(GameSession session)
    {
        foreach (var f in Players.Concat(Pets).Where(f => ReferenceEquals(f.Session, session)))
            f.Left = true;
    }

    /// <summary>The lock that guards the battle while a round is played.</summary>
    public Lock Lock { get; } = new();

    /// <summary>The role (index) of the map NPC that started the battle, 0 when none did.</summary>
    public ushort SourceRole { get; init; }

    /// <summary>True when a map NPC's event started the battle, so that winning removes that NPC.</summary>
    public bool StartedByMapNpcEvent => SourceRole != 0 && !Standalone;

    /// <summary>The monsters waiting to enter the field, each with the position it wants, 0 for any free cell.</summary>
    public List<(NpcRecord Npc, byte Position)> Reinforcements { get; } = [];

    /// <summary>The monsters caught during the battle, with the player who caught each.</summary>
    public List<(Fighter Catcher, NpcRecord Npc)> Caught { get; } = [];

    /// <summary>True when every monster is down and no reinforcement is waiting.</summary>
    public bool AllMonstersDown => Monsters.TrueForAll(m => !m.IsAlive) && Reinforcements.Count == 0;

    /// <summary>Finds the fighter standing in a cell, preferring one that is alive.</summary>
    /// <param name="row">The row of the cell.</param>
    /// <param name="col">The column of the cell.</param>
    /// <returns>The fighter there, or null.</returns>
    public Fighter? At(byte row, byte col) =>
        All.Where(f => f.Row == row && f.Col == col).OrderByDescending(f => f.IsAlive).FirstOrDefault();

    /// <summary>Finds the fighters a skill's target area reaches around the chosen fighter, on the chosen fighter's side.</summary>
    /// <param name="chosen">The fighter the skill was cast on; always part of the area.</param>
    /// <param name="area">
    /// The Skill.dat area code, with the shapes of the client's hit marking: 2 adds the fighter in the other row of the same column,
    /// 3 the fighters above and below in the same row, 4 both, 5 the whole row, 6 the three columns around the chosen fighter in the back
    /// row when the chosen fighter stands in the front row, 7 the three columns around the chosen fighter in both rows, 8 every fighter of
    /// the side; any other code is the chosen fighter alone.
    /// </param>
    /// <returns>The chosen fighter, then the living fighters of the other cells of the area.</returns>
    public List<Fighter> AreaAround(Fighter chosen, byte area)
    {
        (int Row, int Col) facing = (chosen.Row is 1 or 3 ? chosen.Row + 1 : chosen.Row - 1, chosen.Col); // rows 1-2 and 3-4 face each other on a side
        var front = chosen.Row is 2 or 3; // rows 2 and 3 are the front rows
        (int Row, int Col)[] cells = area switch
        {
            2 => [facing],
            3 => [(chosen.Row, chosen.Col - 1), (chosen.Row, chosen.Col + 1)],
            4 => [facing, (chosen.Row, chosen.Col - 1), (chosen.Row, chosen.Col + 1)],
            5 => [.. Enumerable.Range(1, 4).Select(c => (chosen.Row, c))],
            6 when front => [facing, (facing.Row, chosen.Col - 1), (facing.Row, chosen.Col + 1)],
            7 => [facing, (chosen.Row, chosen.Col - 1), (chosen.Row, chosen.Col + 1), (facing.Row, chosen.Col - 1), (facing.Row, chosen.Col + 1)],
            _ => [],
        };
        var others = All.Where(f => f.IsAlive && f.Side == chosen.Side && !ReferenceEquals(f, chosen));
        return [chosen, .. area == 8 ? others : cells.Select(c => others.FirstOrDefault(f => f.Row == c.Row && f.Col == c.Col)).OfType<Fighter>()];
    }

    /// <summary>Tells whether a Skill.dat area code reaches more than the chosen fighter.</summary>
    /// <param name="area">The area code.</param>
    /// <returns>True for the codes <see cref="AreaAround"/> spreads over.</returns>
    public static bool IsSpread(byte area) => area is >= 2 and <= 8;

    /// <summary>Tells the clients which cells around its target a fighter's next action reaches, before the action is sent.</summary>
    /// <param name="attacker">The fighter about to act.</param>
    /// <param name="area">The Skill.dat area code of the action; 1 for an action on its target alone.</param>
    /// <remarks>
    /// The client keeps the area on the fighter and shows an action's values only on the fighters of it, so it is sent before every action,
    /// single-target ones included. The client's codes are the Skill.dat codes less one.
    /// </remarks>
    public void SendAttackArea(Fighter attacker, byte area = 1) =>
        Send(BattleMessages.AttackArea(attacker.Row, attacker.Col, IsSpread(area) ? (byte)(area - 1) : (byte)0));

    /// <summary>Every fighter still present: the players, their pets and the monsters.</summary>
    public IEnumerable<Fighter> All => OwnSide.Concat(Monsters);

    /// <summary>The combat stats source used to look up the fighters' window values, or null.</summary>
    public CombatStats? Combat { get; init; }

    /// <summary>The most HP a monster has.</summary>
    /// <param name="npc">The monster record.</param>
    /// <returns>The HP stored in the record, or a value from its level when it stores none.</returns>
    public static int MonsterMaxHp(NpcRecord npc) => npc.Hp > 0 ? npc.Hp : 40 + 10 * npc.Level;

    /// <summary>The most SP a monster has.</summary>
    /// <param name="npc">The monster record.</param>
    /// <returns>The SP stored in the record, or a value from its level when it stores none.</returns>
    public static int MonsterMaxSp(NpcRecord npc) => npc.Sp > 0 ? npc.Sp : 10 + 5 * npc.Level;

    /// <summary>Maps a monster position number to a cell on the monsters' half of the field.</summary>
    /// <param name="position">The position from 1 to 8 (1 to 4 are row 2, 5 to 8 are row 1).</param>
    /// <returns>The row and column, or null when the position is outside 1 to 8.</returns>
    public static (byte Row, byte Col)? CellOf(byte position) =>
        position is >= 1 and <= 8 ? ((byte)(position <= 4 ? 2 : 1), (byte)((position - 1) % 4 + 1)) : null;

    /// <summary>The eight cells monsters can stand in, in position order.</summary>
    private static readonly ImmutableArray<(byte Row, byte Col)> MonsterCells =
        [.. Enumerable.Range(1, 8).Select(p => CellOf((byte)p)!.Value)];

    /// <summary>Tells whether no living monster stands in a cell.</summary>
    /// <param name="cell">The row and column.</param>
    /// <returns>True when the cell is free.</returns>
    private bool IsFree((byte Row, byte Col) cell) => !Monsters.Exists(m => m.IsAlive && m.Row == cell.Row && m.Col == cell.Col);

    /// <summary>Moves waiting reinforcements onto the field wherever a cell is free.</summary>
    /// <returns>The monster fighters that entered.</returns>
    public List<Fighter> TakeReinforcements()
    {
        List<Fighter> entered = [];
        for (var i = 0; i < Reinforcements.Count; i++)
        {
            var (npc, position) = Reinforcements[i];
            (byte Row, byte Col)? cell = null;
            if (CellOf(position) is { } own)
            {
                if (IsFree(own))
                    cell = own;
            }
            else
            {
                foreach (var c in MonsterCells)
                {
                    if (IsFree(c))
                    {
                        cell = c;
                        break;
                    }
                }
            }
            if (cell is not { } free)
                continue;
            var monster = NewMonster(npc, free.Row, free.Col, Combat);
            Monsters.Add(monster);
            entered.Add(monster);
            Reinforcements.RemoveAt(i--);
        }
        return entered;
    }

    /// <summary>Builds a monster fighter from its record, at full HP and SP.</summary>
    /// <param name="npc">The monster record.</param>
    /// <param name="row">The row of its cell.</param>
    /// <param name="col">The column of its cell.</param>
    /// <param name="combat">The combat stats source for its window values, or null.</param>
    /// <returns>The new fighter.</returns>
    private static Fighter NewMonster(NpcRecord npc, byte row, byte col, CombatStats? combat)
    {
        var hp = MonsterMaxHp(npc);
        var sp = MonsterMaxSp(npc);
        var window = combat?.MonsterValues(npc) ?? new int[5];
        return new Fighter
        {
            Kind = 7, // monster
            Id = npc.Id,
            Row = row,
            Col = col,
            Name = npc.Name,
            MaxHp = hp,
            MaxSp = sp,
            Hp = hp,
            Sp = sp,
            Level = npc.Level,
            Element = npc.Element,
            Strength = npc.Strength,
            Constitution = npc.Constitution,
            Agility = npc.Agility,
            Intelligence = npc.Intelligence,
            Wisdom = npc.Wisdom,
            WindowSpeed = window[CombatStats.SpeedIndex],
            WindowAttack = window[0],
            WindowDefence = window[1],
            WindowMagicAttack = window[2],
            WindowMagicDefence = window[3],
            Npc = npc,
        };
    }

    /// <summary>Creates a battle from an event battle: the player on one side, the event's monsters on the other.</summary>
    /// <param name="character">The character who fights.</param>
    /// <param name="playerId">The player's login id.</param>
    /// <param name="source">The event battle that lists the monsters and their positions.</param>
    /// <param name="npcs">Looks up an NPC record by id.</param>
    /// <param name="sourceRole">The role of the map NPC that started the battle, 0 for none.</param>
    /// <param name="session">The player's session, or null.</param>
    /// <param name="items">Looks up an item record by id, or null when worn items do not count.</param>
    /// <param name="combat">The combat stats source, or null.</param>
    /// <returns>The new battle, with monsters placed and surplus ones waiting as reinforcements.</returns>
    public static Battle Create(
        Character character,
        int playerId,
        EveBattle source,
        Func<ushort, NpcRecord?> npcs,
        ushort sourceRole = 0,
        GameSession? session = null,
        Func<ushort, ItemRecord?>? items = null,
        CombatStats? combat = null)
    {
        var battle = new Battle { EventBattleIndex = source.Index, SourceRole = sourceRole, MapId = character.MapId, Combat = combat };
        battle.AddPlayer(character, playerId, session, PlayerCol, npcs, items);

        var members = source.Left.Where(m => npcs(m.Id) is not null).Select(m => (Npc: npcs(m.Id)!, Position: m.Unknown2)).ToList();
        var placed = new HashSet<int>();
        var taken = new HashSet<byte>();
        for (var i = 0; i < members.Count; i++)
        {
            if (CellOf(members[i].Position) is { } cell && taken.Add(members[i].Position))
            {
                battle.Monsters.Add(NewMonster(members[i].Npc, cell.Row, cell.Col, combat));
                placed.Add(i);
            }
        }
        for (var i = 0; i < members.Count; i++)
        {
            if (!placed.Contains(i))
                battle.Reinforcements.Add(members[i]);
        }
        battle.TakeReinforcements();
        return battle;
    }

    /// <summary>Creates a fight between a player and one map NPC.</summary>
    /// <param name="character">The character who fights.</param>
    /// <param name="playerId">The player's login id.</param>
    /// <param name="npc">The record of the NPC they attack.</param>
    /// <param name="role">The role (index) of the NPC on the map.</param>
    /// <param name="session">The player's session, or null.</param>
    /// <param name="npcs">Looks up an NPC record by id (for the pet), or null.</param>
    /// <param name="items">Looks up an item record by id, or null when worn items do not count.</param>
    /// <param name="combat">The combat stats source, or null.</param>
    /// <returns>The new battle with the NPC as its only monster.</returns>
    public static Battle CreateAgainstMapNpc(
        Character character,
        int playerId,
        NpcRecord npc,
        ushort role,
        GameSession? session = null,
        Func<ushort, NpcRecord?>? npcs = null,
        Func<ushort, ItemRecord?>? items = null,
        CombatStats? combat = null)
    {
        var battle = new Battle { EventBattleIndex = 0, SourceRole = role, Standalone = true, MapId = character.MapId, Combat = combat };
        battle.AddPlayer(character, playerId, session, PlayerCol, npcs ?? (_ => null), items);
        var hp = MonsterMaxHp(npc);
        var sp = MonsterMaxSp(npc);
        var window = combat?.MonsterValues(npc) ?? new int[5];
        battle.Monsters.Add(new Fighter
        {
            Kind = 3, // map NPC
            Id = npc.Id,
            Role = role,
            Row = 2,
            Col = PlayerCol,
            Name = npc.Name,
            MaxHp = hp,
            MaxSp = sp,
            Hp = hp,
            Sp = sp,
            Level = npc.Level,
            Element = npc.Element,
            Strength = npc.Strength,
            Constitution = npc.Constitution,
            Agility = npc.Agility,
            Intelligence = npc.Intelligence,
            Wisdom = npc.Wisdom,
            WindowSpeed = window[CombatStats.SpeedIndex],
            WindowAttack = window[0],
            WindowDefence = window[1],
            WindowMagicAttack = window[2],
            WindowMagicDefence = window[3],
            Npc = npc,
        });
        return battle;
    }

    /// <summary>Adds a player, and their active pet if any, to the battle.</summary>
    /// <param name="character">The character who joins.</param>
    /// <param name="playerId">The player's login id.</param>
    /// <param name="session">The player's session, or null.</param>
    /// <param name="col">The column both fighters stand in.</param>
    /// <param name="npcs">Looks up an NPC record by id, used for the pet.</param>
    /// <param name="items">Looks up an item record by id, or null when worn items do not count.</param>
    /// <param name="side">The side to join, 1 or 2; side 1 stands in the rows 4 and 3, side 2 in the rows 1 and 2.</param>
    /// <returns>The player's fighter.</returns>
    public Fighter AddPlayer(
        Character character,
        int playerId,
        GameSession? session,
        byte col,
        Func<ushort, NpcRecord?> npcs,
        Func<ushort, ItemRecord?>? items = null,
        byte side = 1)
    {
        var (playerRow, petRow) = side == 1 ? (PlayerRow, PetRow) : ((byte)1, (byte)2);
        int Worn(List<EquippedItem> equipment, byte attr) => WornBonus(items, equipment, attr);
        int Critical(List<EquippedItem> equipment) => WornCritical(items, equipment);
        bool Pestle(List<EquippedItem> equipment) => WornPestle(items, equipment);
        var playerWindow = Combat?.Values(character, session is null ? null : CombatStats.Ridden(session)) ?? new int[5];
        var player = new Fighter
        {
            Kind = 2, // player
            Id = playerId,
            Row = playerRow,
            Col = col,
            Name = GmTag.Shown(character.Name, session?.Account),
            MaxHp = character.MaxHp,
            MaxSp = character.MaxSp,
            Hp = Math.Max(1, character.Hp),
            Sp = character.Sp,
            HpSent = character.Hp,
            Level = character.Level,
            Element = character.Element,
            Reborn = character.RebornCount,
            Job = character.Job,
            Strength = character.Strength,
            Constitution = character.Constitution,
            Agility = character.Agility,
            Intelligence = character.Intelligence,
            Wisdom = character.Wisdom,
            Attack = Worn(character.Equipment, AttrAttack),
            Defence = Worn(character.Equipment, AttrDefence),
            WindowSpeed = playerWindow[CombatStats.SpeedIndex],
            WindowAttack = playerWindow[0],
            WindowDefence = playerWindow[1],
            WindowMagicAttack = playerWindow[2],
            WindowMagicDefence = playerWindow[3],
            MagicAttack = Worn(character.Equipment, AttrMagicAttack),
            MagicDefence = Worn(character.Equipment, AttrMagicDefence),
            CriticalPercent = Critical(character.Equipment),
            HalvesTargetSp = Pestle(character.Equipment),
            Session = session,
        };
        Players.Add(player);
        if (character.ActivePetId != 0 && character.Pets.Find(p => p.NpcId == character.ActivePetId) is { } pet)
            AddPet(pet, player, session, petRow, npcs, items);
        return player;
    }

    /// <summary>Adds a player's pet to the battle in the player's column.</summary>
    /// <param name="pet">The pet.</param>
    /// <param name="player">The owner's fighter.</param>
    /// <param name="session">The owner's session, or null.</param>
    /// <param name="petRow">The row pets of the owner's side stand in.</param>
    /// <param name="npcs">Looks up an NPC record by id.</param>
    /// <param name="items">Looks up an item record by id, or null when worn items do not count.</param>
    /// <returns>The pet's fighter.</returns>
    public Fighter AddPet(Pet pet, Fighter player, GameSession? session, byte petRow, Func<ushort, NpcRecord?> npcs, Func<ushort, ItemRecord?>? items)
    {
        var playerId = player.Id;
        var petWindow = Combat?.PetValues(pet) ?? new int[5];
        var fighter = new Fighter
        {
            Kind = 4, // pet
            Id = pet.NpcId,
            OwnerId = playerId,
            Row = petRow,
            Col = player.Col,
            Name = pet.Name,
            MaxHp = pet.MaxHp,
            MaxSp = pet.MaxSp,
            Hp = Math.Max(1, pet.Hp),
            Sp = pet.Sp,
            HpSent = pet.Hp,
            Level = pet.Level,
            Element = pet.Element,
            Reborn = pet.Reborn,
            Strength = pet.Strength,
            Constitution = pet.Constitution,
            Agility = pet.Agility,
            Intelligence = pet.Intelligence,
            Wisdom = pet.Wisdom,
            Attack = WornBonus(items, pet.Equipment, AttrAttack),
            Defence = WornBonus(items, pet.Equipment, AttrDefence),
            WindowSpeed = petWindow[CombatStats.SpeedIndex],
            WindowAttack = petWindow[0],
            WindowDefence = petWindow[1],
            WindowMagicAttack = petWindow[2],
            WindowMagicDefence = petWindow[3],
            MagicAttack = WornBonus(items, pet.Equipment, AttrMagicAttack),
            MagicDefence = WornBonus(items, pet.Equipment, AttrMagicDefence),
            CriticalPercent = WornCritical(items, pet.Equipment),
            HalvesTargetSp = WornPestle(items, pet.Equipment),
            Npc = npcs(pet.NpcId),
            OwnedPet = pet,
            Session = session,
        };
        Pets.Add(fighter);
        return fighter;
    }

    /// <summary>The bonus of an attribute from worn items that are not destroyed, with their gems and fusion.</summary>
    /// <param name="items">Looks up an item record by id, or null when worn items do not count.</param>
    /// <param name="equipment">The worn items.</param>
    /// <param name="attr">The attribute id.</param>
    /// <returns>The bonus.</returns>
    private static int WornBonus(Func<ushort, ItemRecord?>? items, List<EquippedItem> equipment, byte attr) =>
        items is null
            ? 0
            : equipment.Sum(
                e =>
                    !Durability.IsDestroyed(e.Wear) && items(e.ItemId) is { } r
                        ? Gems.Effect(r, e.Insert == 0 ? null : items(e.Insert), attr, e.Fusion)
                        : 0);

    /// <summary>The critical hit percent of the worn weapon and its gem.</summary>
    /// <param name="items">Looks up an item record by id, or null when worn items do not count.</param>
    /// <param name="equipment">The worn items.</param>
    /// <returns>The percent.</returns>
    private static int WornCritical(Func<ushort, ItemRecord?>? items, List<EquippedItem> equipment) =>
        items is null ? 0 : equipment.Where(e => e.Slot == Gems.WeaponSlot).Sum(e =>
            (e.Insert != 0 ? Gems.CriticalPercent(items(e.Insert)) : 0)
                + (Durability.IsDestroyed(e.Wear) ? 0 : Gems.WeaponCriticalPercent(items(e.ItemId))));

    /// <summary>Whether the worn weapon halves the target's SP (a pestle).</summary>
    /// <param name="items">Looks up an item record by id, or null when worn items do not count.</param>
    /// <param name="equipment">The worn items.</param>
    /// <returns>True for a working pestle.</returns>
    private static bool WornPestle(Func<ushort, ItemRecord?>? items, List<EquippedItem> equipment) =>
        items is not null && equipment.Exists(e => e.Slot == Gems.WeaponSlot && !Durability.IsDestroyed(e.Wear) && Gems.HalvesTargetSp(items(e.ItemId)));

    /// <summary>Orders the fighters for the viewer's fight start packet: the viewer's own fighters first, then the rest.</summary>
    /// <param name="viewer">The player fighter the packet is for.</param>
    /// <returns>The viewer, their pet, then the other players with their pets, then the monsters.</returns>
    public IEnumerable<Fighter> FieldOrderFor(Fighter viewer)
    {
        var pet = PetOf(viewer);
        IEnumerable<Fighter> own = pet is null ? [viewer] : [viewer, pet];
        return own.Concat(FieldOrder().Where(f => f != viewer && f != pet));
    }

    /// <summary>Orders the fighters as players with their pets, then the monsters.</summary>
    /// <returns>The fighters in field order.</returns>
    public IEnumerable<Fighter> FieldOrder() =>
        Players.SelectMany(p => PetOf(p) is { } pet ? new[] { p, pet } : [p]).Concat(Monsters);
}
