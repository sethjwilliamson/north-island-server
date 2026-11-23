namespace NorthIsland.Server.Features.Battles;

/// <summary>The tunable numbers of battles: encounters, timing, combinations, experience, damage, flee, drops and rewards.</summary>
public sealed class BattleOptions
{
    /// <summary>How many seconds a monster that a player fled from stays hidden on the map.</summary>
    public int FledMonsterHideSeconds { get; set; } = 10;

    /// <summary>The chance in percent that a step of a free player starts a field fight; 0 turns field fights off.</summary>
    public int FieldEncounterPercent { get; set; } = 8;

    /// <summary>The seconds after a field fight (and after arriving on a map) before the next field fight can start.</summary>
    public int FieldEncounterCooldownSeconds { get; set; } = 30;

    /// <summary>How close in pixels a field monster's place or wander box must be to the player to be fought.</summary>
    public int FieldEncounterRadius { get; set; } = 500;

    /// <summary>True when a field fight rolled inside an encounter zone of the map (a mine, 地雷塊) is taken from the zone's battle list; false fights only the field monsters around.</summary>
    public bool FieldEncounterZones { get; set; } = true;

    /// <summary>The chance in percent that a field fight in an encounter zone comes from the zone rather than from field monsters that are near too; the other is tried when the first starts no battle.</summary>
    public int FieldEncounterZonePercent { get; set; } = 50;

    /// <summary>A fixed pause in milliseconds between the actions of a round; 0 uses the animation lengths of the skills instead.</summary>
    public int BattleActionDelayMs { get; set; }

    /// <summary>Extra milliseconds added to the animation time when the server waits for the client to play a combination or knock-off.</summary>
    public int BattleActionMarginMs { get; set; } = 300;

    /// <summary>Milliseconds the client's battle loop spends drawing per tick, used to estimate how long an action plays.</summary>
    public int BattleTickDrawMs { get; set; } = 14;

    /// <summary>The display refresh rate in Hz the battle loop waits for per tick; 0 means no wait.</summary>
    public int BattleTickVerticalBlankHz { get; set; } = 60;

    /// <summary>Milliseconds the camera needs to glide back after an attacker returns to its cell.</summary>
    public int BattleTickCameraMs { get; set; } = 250;

    /// <summary>Seconds added to the command time before the server plays a round with the commands it has.</summary>
    public int BattleCommandGraceSeconds { get; set; } = 3;

    /// <summary>The largest difference in turn speed between two fighters that can still join the same combination.</summary>
    public int ComboSpeedRange { get; set; } = 100;

    /// <summary>What decides whether a further attacker joins a combination: turn speed alone, or speed plus the level odds below.</summary>
    public ComboJoinRule ComboJoinRule { get; set; }

    /// <summary>The level lead of the attacking side over the target at which a further attacker always joins a combination; used by the level rule only.</summary>
    public int ComboFullLevelGap { get; set; } = 25;

    /// <summary>The level lead from which <see cref="ComboHighPercent"/> is the join chance.</summary>
    public int ComboHighLevelGap { get; set; } = 20;

    /// <summary>The join chance in percent at <see cref="ComboHighLevelGap"/>.</summary>
    public int ComboHighPercent { get; set; } = 90;

    /// <summary>The level lead from which <see cref="ComboMidPercent"/> is the join chance.</summary>
    public int ComboMidLevelGap { get; set; } = 15;

    /// <summary>The join chance in percent at <see cref="ComboMidLevelGap"/>.</summary>
    public int ComboMidPercent { get; set; } = 75;

    /// <summary>The join chance in percent below the mid gap; with the sliding curve, the value the chance starts from.</summary>
    public int ComboBasePercent { get; set; } = 50;

    /// <summary>How the join chance of a combination depends on the level lead.</summary>
    public ComboChanceCurve ComboChanceCurve { get; set; }

    /// <summary>Which fighters' levels are averaged for the combination odds.</summary>
    public ComboOddsLevels ComboOddsLevels { get; set; }

    /// <summary>The levels a fighter counts per rebirth on top of its level for the combination odds.</summary>
    public int ComboRebornLevelBonus { get; set; } = 100;

    /// <summary>The percent added to the damage of every member of a combination; 0 is the plain sum.</summary>
    public int ComboDamageBonusPercent { get; set; }

    /// <summary>How many seconds a monster that was beaten stays hidden on the map.</summary>
    public int DefeatedMonsterHideSeconds { get; set; } = 30;

    /// <summary>
    /// True when the fight against a roaming monster takes it out of play only for the players in that fight, so everyone has their own respawn
    /// of it; stationary monsters, instances and monsters that count toward an instance goal always share one.
    /// </summary>
    public bool PersonalRoamingMonsters { get; set; }

    /// <summary>How many seconds after a battle a player cannot be drawn into a new fight.</summary>
    public int BattleImmunitySeconds { get; set; } = 5;

    /// <summary>Which formula decides the experience a monster pays: the classic monster-level terms or a share of the fighter's level.</summary>
    public ExpMode ExpMode { get; set; } = ExpMode.Classic;

    /// <summary>The base term of the kills a first-run fighter (no rebirth) needs per level in level-share mode.</summary>
    public double KillsPerLevelBase { get; set; } = 5;

    /// <summary>The factor on the fighter's level raised to <see cref="KillsPerLevelPower"/> in the kills per level of a first-run fighter.</summary>
    public double KillsPerLevelCoef { get; set; } = 0.5;

    /// <summary>The power the fighter's level is raised to in the kills per level of a first-run fighter.</summary>
    public double KillsPerLevelPower { get; set; } = 2;

    /// <summary>The base term of the kills a reborn fighter needs per level in level-share mode.</summary>
    public double RebornKillsPerLevelBase { get; set; } = 5;

    /// <summary>The factor on the fighter's level raised to <see cref="RebornKillsPerLevelPower"/> in the kills per level of a reborn fighter.</summary>
    public double RebornKillsPerLevelCoef { get; set; } = 1.13;

    /// <summary>The power the fighter's level is raised to in the kills per level of a reborn fighter.</summary>
    public double RebornKillsPerLevelPower { get; set; } = 2;

    /// <summary>The percent more experience per level a monster is above the fighter, in level-share mode; 0 or more.</summary>
    public double FightUpBonusPerLevelPercent { get; set; } = 1.5;

    /// <summary>The largest percent more experience a monster above the fighter pays, in level-share mode; 0 or more.</summary>
    public double FightUpBonusMaxPercent { get; set; } = 50;

    /// <summary>
    /// The highest fighter level used when a fighter above a monster is compared with it for experience, in both modes; a monster
    /// above the fighter's real level is still measured from that level. 0 or less turns it off.
    /// </summary>
    public int LevelGapCeiling { get; set; } = 165;

    /// <summary>The base term of the experience a monster pays (classic mode).</summary>
    public double MonsterExpBase { get; set; } = 1.5;

    /// <summary>The experience term a monster pays per monster level (classic mode).</summary>
    public double MonsterExpPerLevel { get; set; } = 0.5;

    /// <summary>The experience term added per level the monster is above the fighter (classic mode).</summary>
    public double ExpPerLevelUnderMonster { get; set; } = 1.2;

    /// <summary>The experience term subtracted per level the monster is below the fighter (classic mode).</summary>
    public double ExpPerLevelOverMonster { get; set; } = 0.6;

    /// <summary>The share of a monster's experience a fighter earns for felling it alone.</summary>
    public double ExpSoloKillFactor { get; set; } = 1.0;

    /// <summary>The share of a monster's experience a fighter earns for felling it in a combination.</summary>
    public double ExpComboKillFactor { get; set; } = 1.1;

    /// <summary>The share of a monster's experience a fighter earns for hitting it without felling it.</summary>
    public double ExpHitFactor { get; set; } = 0.5;

    /// <summary>The share added to the experience for each action after the first on a monster.</summary>
    public double ExpPerExtraAction { get; set; } = 0.25;

    /// <summary>The level lead over a monster up to which its experience is paid in full.</summary>
    public int LevelGapFullUpTo { get; set; } = 5;

    /// <summary>The level lead over a monster from which no experience is paid.</summary>
    public int LevelGapZeroFrom { get; set; } = 20;

    /// <summary>The damage factor when the attacker's element overcomes the target's.</summary>
    public double ElementAdvantageMultiplier { get; set; } = 2.0;

    /// <summary>The damage factor when the target's element overcomes the attacker's.</summary>
    public double ElementDisadvantageMultiplier { get; set; } = 0.5;

    /// <summary>The experience a win on the Training Island on top of the normal amount, for a player and a pet; events and potions do not raise it.</summary>
    public int TrainingIslandFlatExp { get; set; } = 200;

    /// <summary>True when only the fighters of the felling blow roll a monster's drops, instead of every winner.</summary>
    public bool DropToKillingBlowOnly { get; set; }

    /// <summary>The highest level that still earns burst experience.</summary>
    public int BurstMaxLevel { get; set; } = 180;

    /// <summary>The percent of the combination damage paid as burst experience to a fighter who cast no debuff or buff.</summary>
    public double BurstDebuffBasePercent { get; set; } = 35;

    /// <summary>The percent added to that for each distinct debuff the fighter cast and each distinct buff it put on an ally who did damage.</summary>
    public double BurstDebuffStepPercent { get; set; } = 15;

    /// <summary>The skills that add their Skill.dat power to their damage, comma separated ids.</summary>
    public string PowerDamageSkills { get; set; } = "25067, 15017, 12024";

    /// <summary>The chance in percent that a Fervidity hit puts mess on a target it left standing.</summary>
    public double FervidityMessPercent { get; set; } = 14;

    /// <summary>Which control and debuff statuses can be on an enemy at the same time: one of each kind, or one in all.</summary>
    public DebuffStacking DebuffStacking { get; set; }

    /// <summary>The chance in percent that a control or Seal lands on an enemy whose Wisdom equals the caster's Intelligence.</summary>
    public double StatusChanceBasePercent { get; set; } = 50;

    /// <summary>The percent the chance moves for a full Intelligence lead: base + spread * (INT - WIS) / (INT + WIS).</summary>
    public double StatusChanceSpreadPercent { get; set; } = 60;

    /// <summary>The lowest chance in percent that a control or Seal lands, however much Wisdom the target has.</summary>
    public double StatusChanceMinPercent { get; set; } = 10;

    /// <summary>The highest chance in percent that a control or Seal lands, however much Intelligence the caster has.</summary>
    public double StatusChanceMaxPercent { get; set; } = 95;

    /// <summary>The percent more damage a target under Cuss takes.</summary>
    public double CussDamagePercent { get; set; } = 50;

    /// <summary>The percent more damage a target under Shrink takes.</summary>
    public double ShrinkDamagePercent { get; set; } = 17;

    /// <summary>The percent more damage a frozen target takes.</summary>
    public double FreezeDamagePercent { get; set; } = 6.23;

    /// <summary>True to compute hits from the fighters' status window values instead of from the stats.</summary>
    public bool WindowDamage { get; set; }

    /// <summary>How much of a skill's Skill.dat power it gains with each level above 1 when WindowDamage is on; 0 means the level changes nothing.</summary>
    public double SkillPowerPerLevel { get; set; } = 0.412;

    /// <summary>The factor on the window attack for an attack on one target.</summary>
    public double WindowAttackFactor { get; set; } = 1.4;

    /// <summary>The factor on the window attack for an attack on two targets.</summary>
    public double WindowAttackFactor2Targets { get; set; } = 0.84;

    /// <summary>The factor on the window attack for an attack on four targets.</summary>
    public double WindowAttackFactor4Targets { get; set; } = 0.6;

    /// <summary>The factor on the window attack for an attack on three, five or more targets.</summary>
    public double WindowAttackFactorManyTargets { get; set; } = 0.4;

    /// <summary>Whether the Hot Fire multiplier applies to the attack value or to the finished damage.</summary>
    public HotFireMode HotFireOn { get; set; }

    /// <summary>The multiplier Hot Fire Attack applies.</summary>
    public double HotFireMultiplier { get; set; } = 3;

    /// <summary>The most damage one hit can do when <see cref="WindowDamage"/> is on; 0 or less means no cap.</summary>
    public int MaxDamagePerHit { get; set; } = 65535;

    /// <summary>The percent of their pet's experience a player gets after buffing it.</summary>
    public double BuffSharePercent { get; set; } = 25;

    /// <summary>The largest product of the seasonal and potion experience multipliers; 0 or less means no cap.</summary>
    public double ExpMultiplierCap { get; set; } = 10;

    /// <summary>The divisor of the pet's defence the Defence and Spirit Saddles lend the rider.</summary>
    public int DefenceSaddleDivisor { get; set; } = 5;

    /// <summary>The percent of the mount's SPD a reborn Knight adds to what the mount's saddle lends; 0 turns the bonus off.</summary>
    public int KnightSaddleSpeedPercent { get; set; } = 15;

    /// <summary>A factor applied to the damage monsters do.</summary>
    public double MonsterDamageScale { get; set; } = 1;

    /// <summary>The base chance in percent that a flee attempt succeeds.</summary>
    public int FleeChancePercent { get; set; } = 70;

    /// <summary>How strongly the speed lead changes the flee chance factor.</summary>
    public double FleeSpeedWeight { get; set; }

    /// <summary>How much each level above the enemies' average changes the flee chance factor.</summary>
    public double FleeLevelStep { get; set; }

    /// <summary>The lowest factor applied to <see cref="FleeChancePercent"/>.</summary>
    public double FleeFactorMin { get; set; }

    /// <summary>The highest factor applied to <see cref="FleeChancePercent"/>.</summary>
    public double FleeFactorMax { get; set; }

    /// <summary>The percent of the current level's experience a player loses when they end a battle down; 0 means no loss.</summary>
    public int DefeatExpLossPercent { get; set; } = 10;

    /// <summary>The chance in percent that a defeated monster drops one item of its item list.</summary>
    public int MonsterDropChancePercent { get; set; } = 30;

    /// <summary>
    /// The most rank steps that make an item of a monster's list rarer: each rank an item sits above the list's lowest-ranked item halves its
    /// drop weight, for at most this many ranks, so 4 keeps the rarest item at no less than a sixteenth of the most common one; 0 or less weighs all items alike.
    /// </summary>
    public int MaxDropRankGap { get; set; } = 4;

    /// <summary>Items any monster within a level range may drop on top of its own list, such as the Alchemy Books; empty turns them off.</summary>
    public List<LevelDrop> LevelDrops { get; set; } = [];

    /// <summary>True when winning a battle also pays gold.</summary>
    public bool MonsterGoldRewards { get; set; }

    /// <summary>The lowest gold a monster pays per level; a level 100 monster at 0.1 pays at least 10.</summary>
    public double GoldPerLevelMin { get; set; } = 0.1;

    /// <summary>The highest gold a monster pays per level; a level 100 monster at 1 pays at most 100.</summary>
    public double GoldPerLevelMax { get; set; } = 1.0;
}
