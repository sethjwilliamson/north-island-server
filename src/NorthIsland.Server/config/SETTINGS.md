# Settings

Every settings file is in this `config/` folder. The keys below are in the `Server` section unless they name another one. The
section is merged from `appsettings.json`, `battle.json`, `progression.json`, `world.json` and `mall.json`, in that order.
Environment variables such as `Server__GmAccounts` override them all.

## appsettings.json

| Setting | What it does |
|---|---|
| `BindAddress` | The IP address the game and status listeners bind to |
| `GamePort` | The TCP port of the game connection |
| `StatusPort` | The TCP port of the status connection; 0 turns it off |
| `ServerId` | The server id sent to the client in the server-selected packet and the status record |
| `ServerName` | The server name sent to the client in the server-selected packet |
| `ServerFlag` | The flag sent with the server-selected packet |
| `ServerStatus` | The status byte of the status record |
| `ClientVersions` | The login versions the server accepts; when empty, the 2010 and 2014 versions |
| `ItemDatSize` | The expected size of the client's Item.dat; null for none |
| `EnforceItemDatSize` | True to kick a login whose Item.dat size differs from ItemDatSize |
| `AutoRegister` | True to create an account when an unknown name logs in |
| `SkipCreationCipher` | True to tell the client to skip the cipher step of character creation |
| `AfterCreate` | What happens after a character is created: EnterWorld or CharacterList |
| `GameDataDirectory` | The directory of the client's data files or of the game; empty to search this server's folder, then the default install |
| `DataDirectory` | The directory of the server's own data (accounts and similar files) |
| `LogDirectory` | The directory of the log files |
| `AutosaveMinutes` | The minutes between autosaves; 0 or less turns them off |
| `MonsterDropChancePercent` | The chance in percent that a defeated monster drops one item of its item list |
| `MaxDropRankGap` | How unevenly a monster's item list is weighted: each rank an item sits above the list's lowest-ranked item halves its weight, for at most this many ranks (default 4, so the rarest item drops at least a sixteenth as often as the most common one); 0 weighs every item alike |
| `FieldEncounterPercent` | The chance in percent that a step of a free player starts a field fight; 0 turns field fights off |
| `ExpMultiplierCap` | The largest product of the seasonal and potion experience multipliers; 0 or less means no cap |
| `MonsterDamageScale` | A factor applied to the damage monsters do |
| `TradeOnlineHours` | The hours an account must have been online before it may trade |
| `DefeatExpLossPercent` | The percent of the current level's experience a player loses when they end a battle down; 0 means no loss |
| `StatPointsPerLevel` | Stat points a character gets for every level gained |
| `SkillPointsPerLevel` | Skill points a character gets for every level gained |
| `LogPackets` | True to log every packet in both directions, typed passwords included |
| `MaxConnections` | The most game connections open at once; a connection over it is closed at once. 0 or less means no limit |
| `MaxConnectionsPerAddress` | The most game connections open at once from one IP address; loopback addresses are not limited. 0 or less means no limit |
| `LoginTimeoutSeconds` | The seconds a connection has to log in, counted from when it connects or goes back to the login form, before it is closed (900, 15 minutes: the client connects before it shows the login form); 0 or less means no limit |
| `IdleTimeoutMinutes` | The minutes a logged-in connection may send nothing before it is closed; 0 or less (the default) turns it off, since the client sends no keep-alive and a player who stands still sends nothing |
| `OutboxPackets` | The most packets queued for one client; a client that lets more pile up (it stopped reading) is disconnected. 0 or less counts as 1 |
| `LoginFailuresBeforeLockout` | The failed logins in LoginLockoutSeconds that lock logins out for LoginLockoutSeconds. Failures of one account name from one address lock that name out for that address only, so nobody can lock an owner out of their account; failures of any names from one address (loopback not counted) lock the address out. Across addresses, a name that failed this often only makes each of its logins wait 1 second before the password is checked, and is never refused. 0 or less counts as 1 |
| `LoginLockoutSeconds` | The seconds in which LoginFailuresBeforeLockout failures are counted, and the seconds the lockout then lasts; 0 or less counts as 1 |
| `LoginFailuresPerConnection` | The failed logins after which the connection is told so and closed; 0 or less counts as 1 |
| `RegistrationsPerAddressPerHour` | The accounts one address may create by AutoRegister per hour; loopback addresses are not limited. 0 or less means no limit |
| `LogFileMegabytes` | The size in megabytes at which a log file is closed and the day's next part (`server-yyyyMMdd.2.log` and so on) begun; 0 or less uses 100. Read once at start |
| `LogFilesPerDay` | The most log files (parts) kept per day. Once the day's last part is full, the oldest part is deleted and the others move down one number, so new lines are never dropped and the day keeps its latest ones, up to LogFilesPerDay × LogFileMegabytes; 0 or less uses 10. Read once at start |
| `GmAccounts` | Account names, separated by commas, made GMs when they log in; set it with the environment variable `Server__GmAccounts`. A listed name is never created by AutoRegister: a login with a listed name that has no account is refused, so log in once to create the account before listing its name |
| `Logging` | the log filter |
| `Logging.LogLevel` | the lowest level logged, per source |
| `Logging.LogLevel.Default` | the server's own messages |
| `Logging.LogLevel.Microsoft` | the messages of the .NET framework |

## battle.json

| Setting | What it does |
|---|---|
| `BattleActionDelayMs` | A fixed pause in milliseconds between the actions of a round; 0 uses the animation lengths of the skills instead |
| `BattleActionMarginMs` | Extra milliseconds added to the animation time when the server waits for the client to play a combination or knock-off |
| `BattleTickDrawMs` | Milliseconds the client's battle loop spends drawing per tick, used to estimate how long an action plays |
| `BattleTickVerticalBlankHz` | The display refresh rate in Hz the battle loop waits for per tick; 0 means no wait |
| `BattleTickCameraMs` | Milliseconds the camera needs to glide back after an attacker returns to its cell |
| `BattleCommandGraceSeconds` | Seconds added to the command time before the server plays a round with the commands it has |
| `ComboSpeedRange` | The largest difference in turn speed between two fighters that can still join the same combination |
| `ComboJoinRule` | What decides whether a further attacker joins a combination: Level (the default; speed plus a roll on the level lead, using the settings below) or Speed (always joins within ComboSpeedRange) |
| `ComboFullLevelGap` | The level lead of the attacking side over the target at which a further attacker always joins a combination; this and the settings down to ComboRebornLevelBonus apply only with the Level rule |
| `ComboHighLevelGap` | The level lead from which ComboHighPercent is the join chance |
| `ComboHighPercent` | The join chance in percent at ComboHighLevelGap |
| `ComboMidLevelGap` | The level lead from which ComboMidPercent is the join chance |
| `ComboMidPercent` | The join chance in percent at ComboMidLevelGap |
| `ComboBasePercent` | The join chance in percent below the mid gap; with the sliding curve, the value the chance starts from |
| `ComboChanceCurve` | How the join chance of a combination depends on the level lead: Steps (fixed bands) or Sliding |
| `ComboOddsLevels` | Which fighters' levels are averaged for the combination odds: All, Players or Members |
| `ComboRebornLevelBonus` | The levels a fighter counts per rebirth on top of its level for the combination odds |
| `ComboDamageBonusPercent` | The percent added to the damage of every member of a combination; 0 is the plain sum |
| `DefeatedMonsterHideSeconds` | How many seconds a monster that was beaten stays hidden on the map |
| `PersonalRoamingMonsters` | True to give each player their own respawn of a roaming monster: fighting it hides it only from the players in that fight, and a team member waiting for it to come back stays out of the team's fight against it; anyone may still join a fight against it with the join cursor. Stationary monsters, instances and monsters that count toward an instance goal always share one respawn. False (default) shares every respawn |
| `BattleImmunitySeconds` | How many seconds after a battle a player cannot be drawn into a new fight |
| `MonsterGoldRewards` | True when winning a battle also pays gold |
| `GoldPerLevelMin` | The lowest gold a monster pays per level; the roll starts at level × this (0.1 gives a level 100 monster at least 10) |
| `GoldPerLevelMax` | The highest gold a monster pays per level; the roll ends at level × this (1 gives a level 100 monster at most 100) |
| `FieldEncounterCooldownSeconds` | The seconds after a field fight (and after arriving on a map) before the next field fight can start |
| `FieldEncounterRadius` | How close in pixels a field monster's place or wander box must be to the player to be fought |
| `FieldEncounterZones` | True when a field fight rolled inside an encounter zone of the map (a mine, 地雷塊) is taken from the zone's battle list; false fights only the field monsters around |
| `FieldEncounterZonePercent` | The chance in percent that a field fight in an encounter zone comes from the zone rather than from field monsters that are near too; the other is tried when the first starts no battle |
| `ElementAdvantageMultiplier` | The damage factor when the attacker's element overcomes the target's |
| `ElementDisadvantageMultiplier` | The damage factor when the target's element overcomes the attacker's |
| `DropToKillingBlowOnly` | True when only the fighters of the felling blow roll a monster's drops, instead of every winner |
| `BurstMaxLevel` | The highest level that still earns burst experience |
| `BurstDebuffBasePercent` | The percent of the combination damage paid as burst experience to a fighter who cast no debuff or buff |
| `BurstDebuffStepPercent` | The percent added to that for each distinct debuff the fighter cast on a monster (hit or missed; counted for that monster's combination damage) and each distinct buff it put on an ally who did damage (once a battle, for every monster) |
| `DebuffStacking` | Which control and debuff statuses an enemy can carry at once. PerKind: one of each kind (control, with mess counted as one; Shrink or Cuss; Slowdown, Defence Down or Seal; poison), where a status of a kind already on misses, except that a control replaces a sleep and the same debuff cast again lasts longer. OnePerEnemy: any control or debuff misses while the enemy carries one |
| `StatusChanceBasePercent` | The chance in percent that a control (freeze, sleep, stone, mess and the like) or Seal lands on an enemy whose Wisdom equals the caster's Intelligence. Other debuffs (Shrink, Cuss, Slowdown, Defence Down, poison) do not roll and land whenever `DebuffStacking` lets them; statuses on your own side always land |
| `StatusChanceSpreadPercent` | How far the caster's Intelligence against the target's Wisdom moves that chance: base + spread * (INT - WIS) / (INT + WIS). With 50 and 60, equal stats give 50%, double the Wisdom 30%, triple 20%, double the Intelligence 70% |
| `StatusChanceMinPercent` | The lowest chance in percent that a control or Seal lands |
| `StatusChanceMaxPercent` | The highest chance in percent that a control or Seal lands |
| `CussDamagePercent` | The percent more damage a target under Cuss takes |
| `ShrinkDamagePercent` | The percent more damage a target under Shrink takes |
| `FreezeDamagePercent` | The percent more damage a frozen target takes |
| `WindowDamage` | True to compute hits from the fighters' status window values instead of from the stats |
| `SkillPowerPerLevel` | How much of a skill's power it gains with each level above 1 when WindowDamage is on: 0.412 makes level 18 eight times level 1; 0 means the level changes nothing |
| `HotFireOn` | Whether the Hot Fire multiplier applies to the attack value (Attack) or to the finished damage (Damage) |
| `HotFireMultiplier` | The multiplier Hot Fire Attack applies |
| `MaxDamagePerHit` | The most damage one hit can do when WindowDamage is on; 0 or less means no cap |
| `FervidityMessPercent` | The chance in percent that a Fervidity hit puts mess on a target it left standing |
| `BuffSharePercent` | The percent of their pet's experience a player gets after buffing it |
| `DefenceSaddleDivisor` | The divisor of the pet's defence the Defence and Spirit Saddles lend the rider |
| `KnightSaddleSpeedPercent` | The percent of the mount's SPD a reborn Knight adds to what the mount's saddle lends (the Tenacity Saddle included); 0 turns the bonus off |
| `FleeChancePercent` | The base chance in percent that a flee attempt succeeds |
| `FleeSpeedWeight` | How strongly the speed lead changes the flee chance factor |
| `FleeLevelStep` | How much each level above the enemies' average changes the flee chance factor |
| `FleeFactorMin` | The lowest factor applied to FleeChancePercent |
| `FleeFactorMax` | The highest factor applied to FleeChancePercent |
| `CapturedPetAmity` | The amity a captured monster joins with; 0 means the usual amity of a new pet |
| `CaptureBasePercent` | The base chance in percent of a capture, before the missing-HP part and the level factor |
| `CaptureHpPercent` | The chance in percent a capture gains when all of the monster's HP is missing, scaled by the share that is missing |
| `CaptureLevelStep` | How much the capture level factor changes for each level the catcher is above the monster |
| `CaptureLevelFactorMin` | The lowest capture level factor, which is also the factor for a monster above the catcher's level |
| `CaptureLevelFactorMax` | The highest capture level factor |
| `CaptureLevelsAbove` | How many levels above the catcher a monster may be and still be captured |
| `MonsterNetLevels` | The levels added to CaptureLevelsAbove while the catcher wears a Monster Net that is not used up |
| `CaptureHpBelowPercent` | A monster whose HP is this percent of its maximum or more cannot be captured; 0 turns the rule off |
| `LevelDrops` | Items any monster in a level range may drop on top of its own item list (by default the Alchemy Books); an empty list turns them off |

### Rows of `LevelDrops`

Each row: Item = the item id that drops; MinLevel = the lowest monster level the row applies to; MaxLevel = the highest (0 means no upper bound); Percent = the chance in percent that a monster in the range drops the item. A monster rolls this table once, separately from its own list and the seasonal event table, and a Double Drop does not double it. The default bands (50-69, 70-89, 90-109, 110+) follow the PC-era Alchemist Codex guide; the WLO Mobile guide puts the Book 3/4 split at 120 instead. No source gives the drop rate, so 0.5 is a guess.

| Item | Name |
|---|---|
| 34009 | Alchemy Book 1 |
| 34010 | Alchemy Book 2 |
| 34011 | Alchemy Book 3 |
| 34012 | Alchemy Book 4 |

## progression.json

| Setting | What it does |
|---|---|
| `ExpMode` | Which formula decides the experience a monster pays: Classic (the default; the monster-level terms below) or LevelShare (each kill pays the experience the fighter needs for its next level divided by the kills per level below, so the grind follows a set curve) |
| `KillsPerLevelBase` | The base term of the kills a first-run fighter needs per level in LevelShare mode (Base + Coef * level ^ Power, at least 1) |
| `KillsPerLevelCoef` | The factor on the fighter's level raised to KillsPerLevelPower in the kills per level of a first-run fighter (LevelShare mode) |
| `KillsPerLevelPower` | The power the fighter's level is raised to in the kills per level of a first-run fighter (LevelShare mode) |
| `RebornKillsPerLevelBase` | The base term of the kills a reborn fighter needs per level (LevelShare mode) |
| `RebornKillsPerLevelCoef` | The factor on the fighter's level raised to RebornKillsPerLevelPower in the kills per level of a reborn fighter (LevelShare mode) |
| `RebornKillsPerLevelPower` | The power the fighter's level is raised to in the kills per level of a reborn fighter (LevelShare mode) |
| `FightUpBonusPerLevelPercent` | The percent more experience per level a monster is above the fighter (LevelShare mode), 0 or more; a monster at or below the fighter uses LevelGapFullUpTo and LevelGapZeroFrom instead |
| `FightUpBonusMaxPercent` | The largest percent more experience a monster above the fighter pays (LevelShare mode), 0 or more |
| `LevelGapCeiling` | The highest fighter level used when a fighter above a monster is compared with it for experience, in both modes, so fighters above the top monsters still earn from them; a monster above the fighter's real level is still measured from that level (no fight-up bonus or under term for a monster below it); 0 or less turns it off |
| `MonsterExpBase` | The base term of the experience a monster pays (Classic mode) |
| `MonsterExpPerLevel` | The experience term a monster pays per monster level (Classic mode) |
| `ExpPerLevelUnderMonster` | The experience term added per level the monster is above the fighter (Classic mode) |
| `ExpPerLevelOverMonster` | The experience term subtracted per level the monster is below the fighter (Classic mode) |
| `ExpSoloKillFactor` | The share of a monster's experience a fighter earns for felling it alone |
| `ExpComboKillFactor` | The share of a monster's experience a fighter earns for felling it in a combination |
| `ExpHitFactor` | The share of a monster's experience a fighter earns for hitting it without felling it |
| `ExpPerExtraAction` | The share added to the experience for each action after the first on a monster |
| `LevelGapFullUpTo` | The level lead over a monster up to which its experience is paid in full |
| `LevelGapZeroFrom` | The level lead over a monster from which no experience is paid |
| `TrainingIslandFlatExp` | The experience a win on the Training Island adds on top of the normal amount, for a player and a pet; events and potions do not raise it. Only while a Training Voucher, Ticket or Fare is running: without one, a player meets no random fights on the island or its areas unless a teammate there is in training, and a win gives them no experience at all and no per-round heal (drops still come) |
| `PetDefeatAmityLoss` | The amity a pet loses when it falls in a battle |
| `PetDefeatExpLossPercent` | The percent of the current level's exp a pet loses when it falls; 0 means it loses amity only |
| `PetAmityPerLevel` | The amity a pet gains for each level it gains |
| `PetRunAwayAmity` | The amity below which a pet that fell can run away; 0 means no pet runs away |
| `PetRunAwayChancePercent` | The chance in percent (0..100) that a pet below PetRunAwayAmity runs away after a fall |
| `PetStatsPerLevel` | The ability points a pet gains for each level, each given to a random ability that is below 602 above its potential floor |

## world.json

| Setting | What it does |
|---|---|
| `Spawn` | Where new characters start |
| `Spawn.MapId` | The map id |
| `Spawn.X` | The x position on the map |
| `Spawn.Y` | The y position on the map |
| `StarterKit` | The items a new character starts with |
| `StarterKit.Enabled` | Whether new characters get the starter kit |
| `StarterKit.Equip` | Item ids put on after the role's default outfit |
| `StarterKit.Bag` | Item ids put in the bag |
| `BagSortFirst` | The item ids the bag sort puts first, in this order: Notepad, Remote Control, Electron Control, Tent |
| `NpcSpawnState` | The state sent for scene objects at map entry; a value outside 0 to 255 sends none |
| `GroundItemRespawnSeconds` | The seconds until a taken map item is back on the ground |
| `DroppedItemSeconds` | The seconds a dropped item stays on the ground |
| `DropScatterPixels` | How far in pixels, on each axis, a dropped item or gold can land from the player; 0 drops it at their feet |
| `CompassStreakForLife` | The right answers in a row that give a Golden Compass player a life back; 0 or less turns it off |
| `LifeSkillExp` | The skill experience a life skill gains per use: an item gathered, a fish caught, or a compound that makes an item |
| `Durability` | Equipment wear settings |
| `Durability.WearPerBattle` | The wear added to each worn piece after a battle that wears gear; 0 switches wear off |
| `Durability.WearMinLevelGap` | How many levels the highest monster must be above the player for a battle to wear gear; 0 or less makes every battle wear |
| `Durability.GunWearPerBattle` | The wear a gun gets after every battle, whatever the level gap; 0 for none |
| `Durability.PestleWearPerBattle` | The wear the Attack Pestle gets after every battle, whatever the level gap; 0 for none |
| `WorldChatSp` | The SP a world chat message costs a player wearing the Radio Set |
| `Guilds` | The rules for guilds, such as the dismissal checks |
| `Guilds.AutoDismiss` | Whether guilds that miss the checks are dismissed |
| `Guilds.RecruitDays` | Days after founding in which the guild must recruit enough members |
| `Guilds.RecruitMembers` | How many members of RecruitLevel the guild needs after RecruitDays |
| `Guilds.RecruitLevel` | The level the recruited members need |
| `Guilds.OnlineDays` | Days after founding in which enough members must be online together once |
| `Guilds.OnlineMembers` | How many members must be online together once |
| `Guilds.SweepMinutes` | Minutes between two dismissal checks; 0 or less switches the check off |
| `Guilds.MaxGuilds` | The most guilds the server holds; 0 means no limit |
| `Guilds.BadgeUploadSeconds` | The seconds a guild waits between two badge uploads; 0 or less means no wait |
| `HotSpring` | Settings of the hot springs |
| `HotSpring.Enabled` | Switches the hot springs on or off |
| `HotSpring.MinutesPerPoint` | Minutes in the water per bonus step |
| `HotSpring.MaxPerVisit` | The most steps one visit gives |
| `HotSpring.PointsPerStep` | The bonus points added to the character's maximum HP and SP per step |
| `HotSpring.MaxTotal` | The most bonus points a character collects at the hot springs in all; the bonus is permanent, so without a cap it would grow for as long as the player bathes. 100 is ten full visits at the settings above (half an hour each). A character already above it keeps what it has and gains no more; 0 or less gives no bonus at all |
| `Sitting` | Settings of the HP and SP recovery while sitting on the ground (the Social window's sit-down action, Alt+1 by default); the rates are guesses from forum posts |
| `Sitting.Enabled` | Switches the recovery on or off |
| `Sitting.TickSeconds` | Seconds between two recovery steps |
| `Sitting.HpPerTick` | The HP one step gives back; 0 for none |
| `Sitting.SpPerTick` | The SP one step gives back; 0 for none |
| `Sitting.ActivePet` | True to give the active pet the same step |

## mall.json

| Setting | What it does |
|---|---|
| `ItemMall` | The item mall settings and catalog |
| `ItemMall.RotationGroups` | Number of rotation groups; 0 turns rotation off |
| `ItemMall.RotationDays` | Days each rotation group stays on sale |
| `ItemMall.Items` | The configured mall rows; when empty, DefaultItems is used |

### Rows of `ItemMall.Items`

Each row: Item = the item id sold; Category = the mall tab it appears in; Price = price of one unit in mall points; Hot = hot marker sent to the client (0 or missing for none); Rotation = rotation group (0 or missing means always shown). Rows may also set Pack (items per purchase, 1 if missing) and Discount (percent of the price charged, 100 if missing).

| Item | Name |
|---|---|
| 26001 | Sky Sword Box |
| 26002 | Lord Sword Box |
| 26003 | Beast Fork Box |
| 26004 | Spirit Fork Box |
| 26005 | Polished Wand Box |
| 26006 | Light Wand Box |
| 26007 | Attack Pestle Box |
| 26015 | Crescent Bow Box |
| 26016 | Golden Rings Box |
| 26019 | Thunder Spear Box |
| 34170 | Weapon Model |
| 21159 | Defense Armor |
| 21172 | Fur Clothes (Male) |
| 21173 | Fur Clothes(Female) |
| 21234 | Kanako's Jaket |
| 21235 | Red Bellyband |
| 22166 | Snow Ears |
| 22233 | Thunder Hairpin |
| 22235 | Deity Headscarf |
| 22236 | Fairy Crest |
| 23138 | Gold Rings |
| 23164 | Emerald Armlet |
| 23165 | Plasma Machine |
| 23166 | Demon Bell |
| 23167 | Mayan Gloves |
| 24179 | Cowboy Boots |
| 24180 | Strong Beast Boots |
| 24181 | Angel Rope Boots |
| 24182 | Thunder Boots |
| 25198 | Steel Hammer 99T |
| 25207 | Friendship Brooch |
| 25228 | Steel Hammer 200T |
| 25229 | Steel Hammer 400T |
| 25230 | Steel Hammer 600T |
| 25567 | Energy Source |
| 26008 | Energy Halo Box |
| 26009 | Golden Ears Box |
| 26010 | Stand Ears Box |
| 26011 | Jade Ears Box |
| 26012 | Dark Evil Wing Box |
| 26013 | Angel Wings Box |
| 26014 | Archangel Wings Box |
| 26017 | Purple Wing Robe Box |
| 26018 | Kung-Fu Suit Box |
| 34167 | Sewing Tools |
| 25196 | VIP Card |
| 25208 | Swift Saddle |
| 25218 | Monster Net |
| 25535 | Bash Saddle |
| 25536 | Magic Saddle |
| 25540 | Decelerate Saddle |
| 25604 | Fast Jet |
| 27018 | Braised Tilapia |
| 27019 | Blessing Strawberry |
| 30099 | Key of Gold Box |
| 30101 | Strong Scroll |
| 30124 | Registration Voucher |
| 30133 | Boarded Voucher |
| 30135 | Wishing Coin |
| 32163 | Fruit Cake |
| 32164 | Neapolitan Sundaes |
| 32173 | Pork Bone Noodle |
| 32174 | Pork Miso Noodle |
| 33054 | Lollipop |
| 34006 | Normal GPS |
| 34007 | Carnie Token |
| 34010 | Alchemy Book 2 |
| 34011 | Alchemy Book 3 |
| 34015 | Glazed Stone Bag |
| 34017 | Dog Bone |
| 34018 | Transfer Mirror |
| 34020 | Magic Set Box |
| 34021 | Magic Magnet |
| 34022 | Tent Palette |
| 34025 | Pet Egg |
| 34026 | Protective EXP Pill |
| 34073 | Mixed Noodles |
| 34099 | 2X EXP Copper Card |
| 34100 | 2X EXP Silver Card |
| 34101 | 2X EXP Gold Card |
| 34102 | 4 Leaves Grass |
| 34103 | 5 Leaves Grass |
| 34104 | Repairing Spanner |
| 34105 | Repairing Potion |
| 34119 | Copper Amity Jelly |
| 34120 | Silver Amity Jelly |
| 34121 | Gold Amity Jelly |
| 34128 | Electron Control |
| 34129 | Big Vacuum Cleaner |
| 34132 | Loudspeaker |
| 34133 | Space Contract |
| 34137 | Pet Lethe Scroll |
| 34155 | Small Tear Plier |
| 34160 | Shape Clay |
| 34163 | Teleport Scroll |
| 34164 | Escape Button |
| 34166 | Delay Clock |
| 34168 | Time Mantle |
| 34169 | Bamboo Dragonfly |
| 34171 | Gift Pack |
| 34180 | Stopwatch |
| 34181 | Training Voucher |
| 34192 | War Pack |
| 34193 | Water Pack |
| 34198 | Rebirth Box |
| 34200 | Present Case |
| 34229 | Gear Pack |
| 34235 | Spring Fireworks |
| 34236 | Chili Sauce |
| 34249 | Wasabi |
| 34269 | Potential Pill |
| 34281 | Healing Potion |
| 34282 | SP Potion |
| 34283 | Super HP Potion |
| 34284 | Super SP Potion |
| 35100 | Training Box |
| 37065 | Coal Block |
| 37075 | Liquid Gas |
| 37090 | Wisdom Crystal |
| 37101 | Magic Wool Ball |
| 37143 | Magnet Stone |
| 37248 | High Uranium |
| 37249 | Fine Uranium |
| 37257 | Memory Crystal |
| 47011 | Unleaded Gasoline |
| 47012 | Great Gasoline |
| 47013 | Diesel Oil |
| 60008 | Hot Water |
| 25194 | Fixed Stick |
| 27002 | Great Syrup |
| 27003 | Roca Perfume |
| 27006 | Elin's Oil |
| 27011 | Cotton Candy |
| 27012 | Wolf Deity Bean |
| 27013 | Delicate Riceball |
| 27014 | Great Soup |
| 27015 | Angel's Feather |
| 27016 | Magic Bean |
| 27017 | Mellow Wine |
| 30067 | Yacht Voucher |
| 30072 | Holy Spirit Voucher |
| 30086 | Jade Rabbit Voucher |
| 30119 | Snow Monster Voucher |
| 30121 | Mammoth Voucher |
| 30163 | Christmas Bell |
| 30178 | Cocoa Nib |
| 30179 | Soft Sweet |
| 30238 | Gold Griffin Voucher |
| 30303 | Cerberus Ticket |
| 33027 | Grenade |
| 33053 | Korean Cake |
| 33055 | Big Bowling |
| 34012 | Alchemy Book 4 |
| 34030 | Glaze Bag |
| 34153 | Sky Banger |
| 34154 | Dragon Banger |
| 34172 | 7 Colors Egg |
| 34199 | Strengthen Pack |
| 34201 | Transferred Pack |
| 34234 | New Year Box |
| 34240 | Spring Riddlepaper |
| 34241 | New Year Pack |
| 34248 | Scroll Pack |
| 34302 | Anniversary Big Bag |
| 34306 | Crystal Ice Cube |
| 34309 | Super Gift Box |
| 34311 | Friendship Pumpkin |
| 34316 | Thanksgiving Egg |
| 34396 | 2010 Jolly Pack |
| 35103 | Maid Box |
| 55009 | Super Reunion Pack |
| 55013 | 2011 Ghost Pack |
| 55017 | 2011 Big Egg |
| 61011 | Nimbus Spar |
| 61031 | Nimbus Diamond |
| 61050 | Dark Spar |
| 61056 | Fairy's Spar |
| 62005 | 18% Bomb Diamond |
| 62006 | 20% Bomb Diamond |
| 34157 | Cherry Door Voucher |
| 34158 | Beach Door Voucher |
| 34175 | Seasonal Suit |
| 34176 | Rabbit Suit |
| 34177 | Cat Suit |

## Settings with no line in a file

These keys of the `Server` section are not in any of the files above; the server uses the default shown. To change one, add it to
any of the merged files (or set it with an environment variable such as `Server__Travel__WalkReachPixels`). A list given that way is
added to the default list, not put in its place, so it can bar more maps or scene types but cannot let back in one the default bars.

| Setting | Default | What it does |
|---|---|---|
| `Travel` | | The rules of travel in the world |
| `Travel.NoTeleportMaps` | 10004, 10009, 10085, 11073, 12064, 12766, 13224 | The maps no teleport may leave or reach, on top of those always barred (scene flag 4, the sea as a destination, houses, instance copies, event arenas and the castle maps): the Prison, the Maze, the Wedding Hall, the Activity Centre's 2F, the Capitol's 2F, the Altar and the Maze in Cave, which the client bars by id |
| `Travel.WalkReachPixels` | 1640 | The farthest, in pixels on either axis, a walk may end from the player's last accepted position; a walk past it is refused and the player put back. The client walks at most 40 cells of 20 pixels and may be up to that far from its last sent end, so a fair walk ends within twice 820 pixels. 0 or less turns the check off |
| `Chat` | | The limits of chat |
| `Chat.MaxTextBytes` | 180 | The most bytes of text one chat line may carry; a longer line is dropped. The client's input box takes 60 characters of up to 3 bytes each. 0 or less turns the check off |
| `Chat.BurstLines` | 5 | The most chat lines a player (not a GM) may send within Chat.BurstSeconds; the line over it is dropped, the client is told its chat is blocked and the player is silenced for Chat.BlockSeconds. 0 or less turns the limit off |
| `Chat.BurstSeconds` | 5 | The window in seconds that Chat.BurstLines counts lines in; 0 or less counts as 1 |
| `Chat.BlockSeconds` | 8 | The seconds a player who went over Chat.BurstLines stays silenced (the client itself holds its chat for 8 seconds once told); 0 or less silences nobody, so only the lines over the limit in the window are dropped |
| `Chat.NoWhisperMaps` | 10701 | The maps whispers cannot be sent from (the client's own list); a whisper sent from one is not passed on |
| `Vehicles` | | The rules of vehicles |
| `Vehicles.NoVehicleMaps` | 10085, 12064, 12065, 12766, 10065, 10066, 10068 | The maps no vehicle may be placed or boarded on, on top of houses and the Quiz Zones: the Wedding Hall, the Capitol's 2F and 4F, the Altar and the three Trojan War rounds, as the client refuses them |
| `Vehicles.NoVehicleSceneTypes` | 8, 9, 11, 14 | The scene types on which no vehicle may be placed or boarded, as the client refuses them (14 is the sea) |
| `HouseLife` | | The numbers for life in the house |
| `HouseLife.FurMailCapacity` | 0 | The most letters a character's Mailbox keeps; 0 or less keeps 100, since a box without a limit could be filled without end |
| `HouseLife.TentReachX` | 800 | The farthest, in pixels across, a player may stand from a tent to go in by clicking it; the client sends the click from anywhere on its 800 by 600 pixel screen, so the default is the screen width. 0 or less means no limit across |
| `HouseLife.TentReachY` | 600 | The farthest, in pixels up or down, a player may stand from a tent to go in by clicking it: by default the screen height. 0 or less means no limit up or down |
| `AnswerSeconds` | 120 | The seconds both partners have to say yes to a marriage application or a divorce form before it is dropped; 0 or less counts as 1 |
| `QuizStartSeconds` | 15 | The seconds the Q&A waits after its first player joins before it asks the first question; players can join only in this time. 0 or less starts at once, so only the first player is in |
| `QuestAbandon` | false | True to let a player give up a quest going on. Off by default: the game's quest window never shows its Delete button, so the normal client never asks, and a quest given up could be done again, with what its steps hand out |

## events.json

```text
The seasonal event calendar and the weekly activity hours.
Copy this file to the data folder (data/events.json) to change it; that copy wins over this one.

events[]:
  name           for the log
  mode           "Auto" (on inside its window), "On" (always, for testing), "Off"
  start, end     yearly window "MM-dd", both days included; the end may be in the next year (12-25 .. 01-01)
  years          per-year windows for the lunar festivals, e.g. "2027": ["06-07", "06-16"]; they replace start / end
                 that year (a lunar festival with no row for this year is off)
  days           the days of the week the event is on, e.g. ["Saturday", "Sunday"]; used instead of the dates above
  expMultiplier  battle exp x this while on (no icon on the client, it just gets the exp)
  drops          the event drop table while on: { item, percent }. A defeated monster rolls it once, apart from its own
                 drop, and gets at most one row (the rows of every event on together; chance = the sum of the percents)
  startNotice    the client's own event line (S 23/57, RecvEventNotice) shown when the event begins and at login while
  endNotice      it is on / to everyone online when it ends; 0 or missing = none. The texts are the client's;
                 which entry an event uses is our choice
  scripts        eve.Emg events whose date conditions (kind 18, pinned to 2006-2009) follow this event instead of the
                 real date: { map, events: [event indices] } (normal and lead events share the numbering)

activities[]: weekly hours of the lobby's Q&A ("quiz") and Golden Compass ("compass"), used only when the server
option ActivitiesAlwaysOpen is false. Server local time.
```

### Notes on single events

Both end dates are included. The lunar festivals move every year, so their dates are listed under `years` in `events.json`.

| Event | When | In the game | Bags (common / big) |
|---|---|---|---|
| Winter Festival | Jan 2 to Jan 21 | Event drops only. | 2012 Sock / 2012 X'Mas Giftbox |
| Chinese New Year | 2 days before to 4 days after the lunar New Year | The Year Beast quest, Aunt Zhuang's fairy and the Red Pack wishes in Fishing Village, plus event drops. | Golden Snake Pack / Deity Dragon Pack |
| Valentine's Day | Feb 7 to Feb 15 | The Western Valentine's quest in Holy Village, plus event drops. | 2013 Valentine Box / 2013 Valentino Pack |
| Lantern Festival | 2 days before to 3 days after the 15th of the 1st lunar month | The Lantern Festival stories in Chang'an, plus event drops. | 2013 Lantern Box / Lantern Blessings Box |
| White Day | Mar 12 to Mar 24 | The White Day event in Holy Village, plus event drops. | White Day Box / 2013 White Day Gift |
| Spring Festival | Mar 28 to Apr 18 | An egg hunt: event drops only. | 2012 Easter Pack / 2012 Super Easter Pack |
| Anniversary | May 1 to May 21 | Event drops only; move it to the server's own anniversary. | 2011 Anniversary bag / Anniversary Big Bag |
| Dragon Boat Festival | 2 days before to 7 days after the 5th of the 5th lunar month | The Dragon Boat quest and Qu Yuan's story in China, Xu Xian's trouble in Chang'an, plus event drops. | Dumpling Box / Golden Dumpling |
| Summer | Jul 1 to Aug 29 | The Summer Event in Holy Village, plus event drops. | Cooling Box / 2011 Summer Gift Pack |
| Artisan Festival | Sep 1 to Sep 14 | Event drops of forging and Lethe scrolls. | Pink Scrollwork Egg / Blue Scrollwork Egg |
| Mid-Autumn Festival | 2 days before to 7 days after the 15th of the 8th lunar month | The Mid-Autumn story in Chang'an, the Jade Rabbit in China, plus event drops. | Reunion Pack / Super Reunion Pack |
| Halloween | Oct 14 to Oct 31 | Double experience and event drops. | 2013 Spooky Pumpkin / 2013 Spooky Pack |
| Thanksgiving | Nov 14 to Nov 30 | The Astrologian in the Civilian's House (map 12003) trades a 2013 Big Turkey and a 2013 Pumpkin Pie, both dropped by monsters, for a 2013 Big Box; repeatable. | 2013 Big Box (for a Turkey and a Pie) / 2013 Big Egg |
| Christmas | Dec 1 to Dec 25 | Santa Claus in Holy Village (4 Happy Xmas Snowballs for a Christmas Gift Pack), Christmas music, plus event drops. | 2013 Sock / 2013 X'Mas Pack |
| New Year | Dec 25 to Jan 1 | Event drops. | 2013 New Year Pack / 2013 Happiness Pack |
| Double XP Weekend | Every Saturday and Sunday | Double experience. | – |

Some event is on about 70% of the days of a year (Double XP Weekend not counted). Ghost Month and Qixi are left out:
they always fall inside Summer, and one inside the other, so their drops would stack. Their quests (the Night Parade in
Fishing Village, Ming and Ling's wedding, the bamboo wishes) only run while a calendar event lists their scripts; to bring
one back, add an event with its `scripts` from the git history of this file.

### Event prizes

The event bags are meant to be the only way to get the Item Mall's items (as if the mall were closed); no bag gives a
pet voucher or the 7 Colors Egg; the one random-pet prize is the Pet Egg (Spring Festival, Anniversary, Thanksgiving),
whose pet starts at level 1 like every new pet. Each event's monsters drop its common bag (1.67% per monster) and, rarely,
its big bag (0.0125%). The rates are set for a fast grinder at 20 kills a minute (1,200 kills an hour): about 20 common
bags an hour and one big bag every 4 hours, counting the big bags found inside common bags (0.1 an hour from those and
0.15 dropped). That works out to about one jackpot every 10 hours and one top prize every 150 hours, before choice cards.
The bags are the client's own lucky bags (item type 41,
which show their contents when previewed), except the Anniversary boxes, and their rows are in `Data/LuckyBags.tsv` and
`Data/BoxGuesses.tsv` (or override them with `MallBoxes:LuckyBagsFile`). Every bag has a few themed rows and the same
fixed rows; the forging scrolls come more often and in bigger stacks the lower their success rate (common bag weights out
of 10,000, big bag weights out of 1,000):

| Bag | Rows |
|---|---|
| Common, themed | a high-HP food ×5 and a high-SP food ×5 (1150 each, 11.5%) and Pizza ×2 (1000, 10%); 4 uncommons such as fireworks, scrolls, amity jellies, Delay Clocks (450 each, 18%); 3 rares such as the suit boxes, ears boxes, the mall's own boxes, Gold Amity Jelly, door vouchers, spars (150 each, 4.5%); one jackpot (15, 0.15%); the event's big bag (50, 0.5%) |
| Common, fixed | Carnie Token ×3 (1000, 10%); Alchemy Book 1 (600, 6%), 2 (500, 5%), 3 (400, 4%) and 4 (300, 3%); the event's common forging scroll ×3 (400, 4%) and uncommon forging scroll ×2, Small Demon ×1 (250, 2.5%); the event's uncommon diamond (150, 1.5%); Strong Scroll and Pet Lethe Scroll (150 each, 1.5%); the event's rare forging scroll (100, 1%); the event's rare diamond (80, 0.8%); Seraph Scroll and the event's jackpot diamond (15 each, 0.15%); a Bronze Card (195, 1.95%), a Silver Card (75, 0.75%) and a Golden Card (5, 0.05%) |
| Big, themed | 4 rares (115 each, 46%); 3 jackpots such as wings and robe boxes, saddles, Water Pack (Holy EXP potions), Transfer Mirror, Maid Box, Bomb Diamonds, Potential Pill (50 each, 15%); 2 top prizes: the mall weapon boxes, Friendship Brooch, Fairy's Spar, Yacht Voucher (12 each, 2.4%) |
| Big, fixed | Strong Scroll ×3 and Pet Lethe Scroll ×2 (70 each, 7%); the event's rare forging scroll ×2 (60, 6%) and Seraph Scroll ×2 (30, 3%); the event's rare diamond (50, 5%), jackpot diamond (15, 1.5%) and top-prize diamond (3, 0.3%); a Silver Card (45, 4.5%), a Golden Card (20, 2%) and a Free Card (3, 0.3%) |

At 20 kills a minute that is about 1.2 Alchemy Book 1s, 1 Book 2, 0.8 Book 3s and 0.6 Book 4s an hour, a
Strong Scroll or a Pet Lethe Scroll every 3 hours, a Seraph Scroll every 22 hours and the event's uncommon diamond every 3
hours. Each event has one diamond of each rarity, and the events take turns through each rarity's range:

| Rarity | Diamonds | Events |
|---|---|---|
| Uncommon (common bag) | Black +20, Shining +22, Nimbus +24 | Black: Winter Festival, Lantern Festival, Anniversary, Artisan Festival, Thanksgiving; Shining: Chinese New Year, White Day, Dragon Boat Festival, Mid-Autumn Festival, Christmas; Nimbus: Valentine's Day, Spring Festival, Summer, Halloween, New Year |
| Rare (both bags) | Pink +26, Golden +28, Orange +30, Grass +32 | Pink: Winter Festival, White Day, Summer, Thanksgiving; Golden: Chinese New Year, Spring Festival, Artisan Festival, Christmas; Orange: Valentine's Day, Anniversary, Mid-Autumn Festival, New Year; Grass: Lantern Festival, Dragon Boat Festival, Halloween |
| Jackpot (both bags) | Sanguine +34, Blove +36, Sky +38, Death +40 | Sanguine: Winter Festival, White Day, Summer, Thanksgiving; Blove: Chinese New Year, Spring Festival, Artisan Festival, Christmas; Sky: Valentine's Day, Anniversary, Mid-Autumn Festival, New Year; Death: Lantern Festival, Dragon Boat Festival, Halloween |
| Top prize (big bag) | Fairy +42, Purple +44, Star +46 | Fairy: Winter Festival, Lantern Festival, Anniversary, Artisan Festival, Thanksgiving; Purple: Chinese New Year, White Day, Dragon Boat Festival, Mid-Autumn Festival, Christmas; Star: Valentine's Day, Spring Festival, Summer, Halloween, New Year |

Every common bag's foods are a high-HP food (HP 800 or more, higher than its SP), a high-SP food (SP 600 or more,
higher than its HP) and Pizza (Hawaii Pizza for Summer, Anniversary and Artisan Festival), which restores both; the
Wishing Coin isn't a prize (its Holy Village pool gifts include Boarded Vouchers and the VIP Card). An event
uses its own food when it has one that fits, and otherwise the events take turns through the strongest foods:

| Event | High-HP food | High-SP food |
|---|---|---|
| Winter Festival | QQ Noodles (HP 1500) | Koryo Insam (SP 1200) |
| Chinese New Year | Nian Congee (HP 800, SP 500) | Stomach Powder (SP 1200) |
| Valentine's Day | Love Chocolate (HP 800, SP 400) | Gyrophora Chicken (SP 935) |
| Lantern Festival | Yo BBQ Sauce (HP 1200) | Ginseng Thick Soup (SP 850) |
| White Day | Passion Chocolate (HP 1200, SP 600) | Takoyaki (SP 700, HP 300) |
| Spring Festival | Go Cole (HP 1000) | Ginseng Chicken (SP 650, HP 175) |
| Anniversary | Soyseafood Rice (HP 900, SP 650) | Asiatic Wormwood (SP 600) |
| Dragon Boat Festival | He Riceball (HP 1200, SP 600) | Koryo Insam (SP 1200) |
| Summer | Hawaii Wine (HP 1000, SP 800) | Stomach Powder (SP 1200) |
| Artisan Festival | He Riceball (HP 1200, SP 600) | Gyrophora Chicken (SP 935) |
| Mid-Autumn Festival | Queen's Peach (HP 1000, SP 500) | Ginseng Thick Soup (SP 850) |
| Halloween | QQ Noodles (HP 1500) | Takoyaki (SP 700, HP 300) |
| Thanksgiving | Turkey (HP 800) | Ginseng Chicken (SP 650, HP 175) |
| Christmas | Turkey (HP 800) | Asiatic Wormwood (SP 600) |
| New Year | Soyseafood Rice (HP 900, SP 650) | Koryo Insam (SP 1200) |

The forging scrolls work the same way: each event has one scroll of each rarity, and the Seraph Scroll (100%) is every
event's jackpot scroll.

No bag has two prizes of the same family in one tier, and no prize twice. A family is what the item does: every pet amity
item (the Amity Jellies, Spring and Bless Jelly, Love Picnic Lunch, Love Bouquets: Item.dat effect 64), high-HP foods,
high-SP foods, pizzas, fireworks, throwables (snowballs, Water Ball, Lantern Cannon), gems (diamonds and spars), Bomb Diamonds, forging scrolls, Wit Maid
suits, ears boxes, wing boxes, weapon boxes, random gift boxes, door vouchers, gear combiners, saddles, outfits, hats,
furniture, EXP items and hammers; anything else is its own family. The four Alchemy Books are the one exception (their
chances were set on purpose), and the 2013 Big Turkey is a bag of food.

| Rarity | Scrolls | Events |
|---|---|---|
| Common (common bag ×3) | Demon 50%, Principality 60% | Demon: Winter Festival, Valentine's Day, White Day, Anniversary, Summer, Mid-Autumn Festival, Thanksgiving, New Year; Principality: Chinese New Year, Lantern Festival, Spring Festival, Dragon Boat Festival, Artisan Festival, Halloween, Christmas |
| Uncommon (common bag ×2) | Archangel 65%, Virtues 70%, Small Demon 50% for +4 (×1) | Archangel: Winter Festival, Lantern Festival, Anniversary, Artisan Festival, Thanksgiving; Virtues: Chinese New Year, White Day, Dragon Boat Festival, Mid-Autumn Festival, Christmas; Small Demon: Valentine's Day, Spring Festival, Summer, Halloween, New Year |
| Rare (common bag ×1, big bag ×2) | Ofanim 80%, Cherubim 90% | Ofanim: Winter Festival, Chinese New Year, White Day, Spring Festival, Summer, Artisan Festival, Thanksgiving, Christmas; Cherubim: Valentine's Day, Lantern Festival, Anniversary, Dragon Boat Festival, Mid-Autumn Festival, Halloween, New Year |
Travel items aren't prizes (Bamboo Dragonfly, Teleport Scroll, GPS, Memory Crystal, also taken out of the War Pack, the
Scroll Pack and the Thanksgiving Egg in `Data/MallBoxes.tsv`); the Yacht Voucher is only a top prize.

The four choice cards (gift cards, `Data/GiftCards.tsv`) are exchanged for any one prize of their tier from every event,
chosen by the player, so a prize whose event is months away is still within reach. Everything a common bag (or the 2013 Big
Turkey) gives stacks, so opening them doesn't fill the bag window; the prizes that don't stack (the timed riceballs, EXP
cards and grass, the bottle potions, costumes, hats and furniture) come from the Bronze and Silver Cards, which do, or from
the big bags. Holiday items fill only their own event's bags.

| Card | Choose one of |
|---|---|
| Bronze Card (50017) | the 56 uncommons, Alchemy Book 1, the Escape Button and the four bottle potions |
| Silver Card (50018) | the 79 rares |
| Golden Card (50019) | the 22 jackpots |
| Free Card (50000) | the 12 top prizes |

A choice card is at most half as likely as the most likely row of its tier in the same bag. At 20 kills a minute that is
about 0.4 Bronze Cards and 0.16 Silver Cards an hour. Outside its own event, getting a chosen prize takes about 2.5 to 6.5
times as many monsters as during it (about 1 in 3,100 kills for an uncommon, 7,400 for a rare, 80,000 for a jackpot and
1,600,000 for a top prize, against 470, 2,700, 18,000 and 320,000 in season). After changing a card's choices, raise `GiftCards.Flag` so clients reload their saved lists.

The mall's real boxes are prizes too (Gift Pack, War Pack, Scroll Pack, Gear Pack with the pet gear, Transferred Pack,
Water Pack, Maid Box, Crystal Ice Cube, Super Gift Box, Friendship Pumpkin, Thanksgiving Egg, 2010 Jolly Pack, Anniversary
Big Bag 34302), with their pet voucher and travel item rows taken out of `Data/MallBoxes.tsv`.

Mall items no event gives, on purpose:

| Why | Items |
|---|---|
| Can be had another way (NPC shop, crafting, monster drops) | Registration Voucher, Boarded Voucher, Sky Banger, Dragon Banger, Pork Bone Noodle, Pork Miso Noodle, Mixed Noodles, Shape Clay, the vehicle fuels |
| Pet food (permanent pet ability points) | the 12 "Adds 1 to ...'s ability" foods, Strengthen Pack |
| Leveled pets | Holy Spirit, Jade Rabbit, Snow Monster, Mammoth and Gold Griffin Vouchers, Cerberus Ticket, 7 Colors Egg (the Pet Egg is the one random-pet prize) |
| No use on this server (yet) | Loudspeaker (no world radio), VIP Card (shops don't discount), Wisdom Crystal, Fast Jet, Christmas Bell, Cocoa Nib, Soft Sweet, Spring Riddlepaper, Fixed Stick, and the battle throwables Grenade, Korean Cake, Big Bowling and Lollipop (using items in battle only heals) |
| Weak food | Fruit Cake, Neapolitan Sundaes |
| Boxes whose contents are given directly | Training Box, New Year Box, New Year Pack, 2011 Ghost Pack, 2011 Big Egg, Glazed Stone Bag, Magic Set Box, Glaze Bag |
| Pet gear, in the Gear Pack | the 13 "Special for ..." pet gear pieces |
| Travel | Bamboo Dragonfly, Teleport Scroll, Normal GPS, Memory Crystal, and the Wishing Coin (its Holy Village pool gifts include 20 Boarded Vouchers and the VIP Card) |

The common bags can't be traded and the big bags (the year "Pack"s) can, as in the original game. The 2013 Big Turkey only
opens into food, so trading it in is worth more; the 2013 Big Box it is traded for is Thanksgiving's common
bag (the Pie and the Turkey drop at 1.67% each, so the Boxes come at the common bag rate), and the 2013 Big Egg its big bag.
Christmas splits its common bags the same way: the Sock drops at 1.25% and the Happy Xmas Snowball at 1.67%, and 4
Snowballs buy a Christmas Gift Pack (1.25 + 1.67 / 4 = 1.67).
The Christmas Gift Pack and the Spring Red Pack (from the Chinese New Year script) use their event's common bag rows.

National Treasure runs on Mondays and the 15-round challenge on Fridays, both from 7 to 9 pm server time.

## quiz.json

Q&A questions (map 10079). 'correct' is 1-4. From a video of the original event; arithmetic questions are also generated (Server:QuizMathPercent). Add more freely. Prizes by final place (players still in at the end), modelled on the 15-round challenge's rewards; a full bag gets nothing.
