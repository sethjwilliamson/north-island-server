using NorthIsland.GameData.Npcs;
using NorthIsland.Protocol.Messages.Battle;

namespace NorthIsland.Server.Features.Battles;

/// <summary>One combatant in a battle: a player, a pet, a map NPC or a monster, with its position, stats, status effects and reward bookkeeping.</summary>
public sealed class Fighter
{
    /// <summary>Combatant type as the battle packets carry it: 2 player, 3 map NPC, 4 pet, 7 monster.</summary>
    public required byte Kind { get; init; }

    /// <summary>Player id for a player, the pet's Npc.dat id for a pet, the Npc.dat id for a monster or map NPC.</summary>
    public required int Id { get; init; }

    /// <summary>Battlefield row; rows 3-4 are the player side, rows 1-2 the opposing side.</summary>
    public required byte Row { get; init; }

    /// <summary>Battlefield column.</summary>
    public required byte Col { get; init; }

    /// <summary>Display name used in the log.</summary>
    public string Name { get; init; } = "";

    /// <summary>Role id of the map NPC this fighter stands for, 0 for the other kinds.</summary>
    public ushort Role { get; init; }

    /// <summary>Maximum HP.</summary>
    public int MaxHp { get; init; }

    /// <summary>Maximum SP.</summary>
    public int MaxSp { get; init; }

    /// <summary>Current HP; the fighter is down at 0 or less.</summary>
    public int Hp { get; set; }

    /// <summary>Current SP.</summary>
    public int Sp { get; set; }

    /// <summary>The HP last sent to the owning player's client, so the round-end update goes out only when it changed.</summary>
    public int HpSent { get; set; }

    /// <summary>Level within the current rebirth.</summary>
    public byte Level { get; init; }

    /// <summary>Rebirth count times 100 plus the level.</summary>
    public int TotalLevel => Reborn * 100 + Level;

    /// <summary>Element id, as sent in the fighter record.</summary>
    public byte Element { get; init; }

    /// <summary>Rebirth count.</summary>
    public byte Reborn { get; init; }

    /// <summary>Job id.</summary>
    public byte Job { get; init; }

    /// <summary>Strength attribute.</summary>
    public int Strength { get; init; }

    /// <summary>Constitution attribute.</summary>
    public int Constitution { get; init; }

    /// <summary>Agility attribute.</summary>
    public int Agility { get; init; }

    /// <summary>Intelligence attribute.</summary>
    public int Intelligence { get; init; }

    /// <summary>Wisdom attribute.</summary>
    public int Wisdom { get; init; }

    /// <summary>Attack bonus from the worn items.</summary>
    public int Attack { get; init; }

    /// <summary>Defence bonus from the worn items.</summary>
    public int Defence { get; init; }

    /// <summary>Magic attack bonus from the worn items.</summary>
    public int MagicAttack { get; init; }

    /// <summary>Magic defence bonus from the worn items.</summary>
    public int MagicDefence { get; init; }

    /// <summary>Speed value of the status window, which already includes the worn items' speed.</summary>
    public int WindowSpeed { get; init; }

    /// <summary>Attack value of the status window, the base the damage formulas start from.</summary>
    public int WindowAttack { get; init; }

    /// <summary>Defence value of the status window.</summary>
    public int WindowDefence { get; init; }

    /// <summary>Magic attack value of the status window.</summary>
    public int WindowMagicAttack { get; init; }

    /// <summary>Magic defence value of the status window.</summary>
    public int WindowMagicDefence { get; init; }

    /// <summary>Chance in percent that a hit of this fighter is critical, from the worn gems and weapon.</summary>
    public int CriticalPercent { get; init; }

    /// <summary>True when the equipped weapon halves the SP of the fighter it hits.</summary>
    public bool HalvesTargetSp { get; init; }

    /// <summary>The Npc.dat record of a monster, map NPC or pet; null for a player.</summary>
    public NpcRecord? Npc { get; init; }

    /// <summary>For a pet, the player id of its owner; 0 otherwise.</summary>
    public int OwnerId { get; init; }

    /// <summary>For a pet fighter, the pet it was built from; null for the other kinds.</summary>
    public Pet? OwnedPet { get; init; }

    /// <summary>True while the fighter has chosen the defend command this round.</summary>
    public bool Defending { get; set; }

    /// <summary>
    /// For a player or pet, the normal attack id its client last sent in this battle (the client picks it from the weapon); 0 until one
    /// was sent. A counter of a player or pet is played with it.
    /// </summary>
    public ushort NormalAttackId { get; set; }

    /// <summary>The session of a player or pet fighter; null for monsters and map NPCs.</summary>
    public GameSession? Session { get; init; }

    /// <summary>True when the player joined an already running battle instead of starting in it.</summary>
    public bool Joined { get; set; }

    /// <summary>The battle round in which the fighter joined, -1 when it was there from the start.</summary>
    public int JoinedRound { get; set; } = -1;

    /// <summary>The battle round in which the player called a pet into the battle, which used the player's turn; -1 when never.</summary>
    public int SummonedRound { get; set; } = -1;

    /// <summary>True once the fighter has left the battle (fled, disconnected or abandoned it).</summary>
    public bool Left { get; set; }

    /// <summary>True when a single hit exceeded twice the player's maximum HP, which sends them back to the starter's beach after defeat.</summary>
    public bool SentBack { get; set; }

    /// <summary>The experience the player lost by being defeated.</summary>
    public int ExpLost { get; set; }

    /// <summary>For a monster, how many actions each fighter made against it and how much damage they did, used to split the rewards.</summary>
    public Dictionary<Fighter, MonsterContact> Contacts { get; } = [];

    /// <summary>Records one action by <paramref name="actor"/> against this fighter.</summary>
    /// <param name="actor">The fighter that attacked or used a skill on this one.</param>
    /// <param name="damage">The damage done; a negative value counts as 0.</param>
    public void NoteAction(Fighter actor, long damage = 0)
    {
        if (!Contacts.TryGetValue(actor, out var contact))
            Contacts[actor] = contact = new MonsterContact();
        contact.Actions++;
        contact.Damage += Math.Max(0, damage);
    }

    /// <summary>Counts the actions <paramref name="actor"/> has made against this fighter.</summary>
    /// <param name="actor">The fighter to look up.</param>
    /// <returns>The number of recorded actions, 0 when there are none.</returns>
    public int ActionsOf(Fighter actor) => Contacts.TryGetValue(actor, out var contact) ? contact.Actions : 0;

    /// <summary>The human fighters credited with the killing blow or the combination that felled this fighter.</summary>
    public List<Fighter> KilledBy { get; } = [];

    /// <summary>The item this monster drops, rolled once; null when none.</summary>
    public ushort? DropItem { get; set; }

    /// <summary>True once the regular drop has been rolled, so it is rolled only once per monster.</summary>
    public bool DropRolled { get; set; }

    /// <summary>The calendar event item this monster drops, rolled once; null when none.</summary>
    public ushort? EventDropItem { get; set; }

    /// <summary>True once the event drop has been rolled.</summary>
    public bool EventDropRolled { get; set; }

    /// <summary>The level drop table item this monster drops, rolled once; null when none.</summary>
    public ushort? LevelDropItem { get; set; }

    /// <summary>True once the level drop has been rolled.</summary>
    public bool LevelDropRolled { get; set; }

    /// <summary>True once the drop was handed to a player.</summary>
    public bool DropGiven { get; set; }

    /// <summary>The total damage done to this monster by combinations of two or more attackers.</summary>
    public long ComboDamage { get; set; }

    /// <summary>The distinct debuff effect bytes this fighter cast on each enemy, hit or missed, for the damage burst.</summary>
    /// <remarks>Kept per enemy: a monster that enters a fallen one's cell is a new fighter and starts with none.</remarks>
    public Dictionary<Fighter, HashSet<byte>> DebuffsCast { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>The buff effect bytes this fighter put on allies and who got them, for the damage burst.</summary>
    public HashSet<(byte Effect, Fighter Target)> BuffsGiven { get; } = [];

    /// <summary>True once this fighter has done damage to an enemy in this battle.</summary>
    public bool DealtDamage { get; set; }

    /// <summary>
    /// Counts the distinct debuffs this fighter cast on an enemy plus the distinct buffs it put on allies who then did damage in the
    /// battle: debuffs count for each enemy, buffs once a battle.
    /// </summary>
    /// <param name="enemy">The enemy whose combination damage the count applies to.</param>
    /// <returns>The number of effects.</returns>
    public int BurstEffectCountOn(Fighter enemy) =>
        (DebuffsCast.TryGetValue(enemy, out var debuffs) ? debuffs.Count : 0)
        + BuffsGiven.Where(b => b.Target.DealtDamage).Select(b => b.Effect).Distinct().Count();

    /// <summary>True when the player cast a buff on their own pet during this battle.</summary>
    public bool BuffedPet { get; set; }

    /// <summary>True for the fighters in rows 3 and 4, the side of the players who started the battle.</summary>
    public bool IsPlayerSide => Row >= 3;

    /// <summary>Battle side: 1 for rows 3-4, 2 for rows 1-2.</summary>
    public byte Side => Row >= 3 ? (byte)1 : (byte)2;

    /// <summary>True for a player or a pet.</summary>
    public bool IsHuman => Kind is 2 or 4; // 2 player, 4 pet

    /// <summary>True for a player in admin mode, who takes no damage.</summary>
    public bool IsInAdminMode => Kind == 2 && Session is { AdminMode: true }; // player

    /// <summary>True while HP is above 0.</summary>
    public bool IsAlive => Hp > 0;

    /// <summary>The status effects currently on the fighter.</summary>
    public List<FighterStatus> Statuses { get; } = [];

    /// <summary>Whether a status with the given Skill.dat effect byte is on the fighter.</summary>
    /// <param name="effect">The Skill.dat effect byte to look for.</param>
    /// <returns>True when such a status is present.</returns>
    public bool HasEffect(byte effect) => Statuses.Exists(s => s.Effect == effect);

    /// <summary>True when a player captured this monster, which takes it out of the reward calculations.</summary>
    public bool Captured { get; set; }

    /// <summary>Builds the fighter record the battle packets carry for this fighter.</summary>
    /// <param name="own">True when the record is for the player's own fighter, which gets <paramref name="ownState"/> as its state.</param>
    /// <param name="ownState">The state value of the player's own record.</param>
    /// <returns>The record, with HP and SP limited to non-negative values.</returns>
    public FighterRecord ToRecord(bool own, byte ownState = 1) => new()
    {
        State = own ? ownState : (byte)0,
        Kind = Kind,
        Id = Id,
        Role = Role,
        OwnerId = OwnerId,
        Row = Row,
        Col = Col,
        MaxHp = Math.Max(0, MaxHp),
        MaxSp = Clamp(MaxSp),
        Hp = Math.Max(0, Hp),
        Sp = Clamp(Sp),
        Level = Level,
        Element = Element,
        Reborn = Reborn,
        Job = Job,
    };

    /// <summary>Limits a value to the range of an unsigned 16-bit number.</summary>
    /// <param name="v">The value.</param>
    /// <returns>The value limited to 0 through 65535.</returns>
    private static ushort Clamp(int v) => (ushort)Math.Clamp(v, 0, ushort.MaxValue);
}
