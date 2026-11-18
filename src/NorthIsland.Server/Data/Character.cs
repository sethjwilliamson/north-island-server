namespace NorthIsland.Server.Data;

/// <summary>A character as stored in its account file: look, stats, items, pets and the state of every feature.</summary>
public sealed class Character
{
    /// <summary>The character slot of the account.</summary>
    public byte Slot { get; set; }

    /// <summary>The character's name.</summary>
    public string Name { get; set; } = "";

    /// <summary>When the character was created (UTC).</summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>The body type chosen at creation; with <see cref="Head"/> it selects the role.</summary>
    public byte BodyType { get; set; }

    /// <summary>The head chosen at creation.</summary>
    public byte Head { get; set; }

    /// <summary>The role (look) an event NPC change waits to apply when the character leaves the map or logs in again; 0 for none.</summary>
    public byte PendingRole { get; set; }

    /// <summary>The sprite (look) sent for the character.</summary>
    public byte Sprite { get; set; }

    /// <summary>The character's first colour.</summary>
    public int Colour1 { get; set; }

    /// <summary>The character's second colour.</summary>
    public int Colour2 { get; set; }

    /// <summary>The character's element.</summary>
    public byte Element { get; set; }

    /// <summary>The five stat choices made at creation, from which the starting attributes are computed.</summary>
    public List<byte> CreationStats { get; set; } = [0, 0, 0, 0, 0];

    /// <summary>The character's own password, set at creation or by the password change; null or empty when none.</summary>
    public string? Cipher { get; set; }

    /// <summary>True once the starting attributes were computed from <see cref="CreationStats"/>.</summary>
    public bool AttributesFromCreation { get; set; }

    /// <summary>The strength attribute.</summary>
    public ushort Strength { get; set; }

    /// <summary>The constitution attribute; it feeds max HP.</summary>
    public ushort Constitution { get; set; }

    /// <summary>The intelligence attribute.</summary>
    public ushort Intelligence { get; set; }

    /// <summary>The wisdom attribute; it feeds max SP.</summary>
    public ushort Wisdom { get; set; }

    /// <summary>The agility attribute.</summary>
    public ushort Agility { get; set; }

    /// <summary>The skill points not yet spent.</summary>
    public ushort SkillPoints { get; set; }

    /// <summary>The attribute points not yet spent.</summary>
    public ushort StatPoints { get; set; }

    /// <summary>The character's potential value.</summary>
    public byte Potential { get; set; }

    /// <summary>The character's level.</summary>
    public byte Level { get; set; } = 1;

    /// <summary>How many times the character has been reborn.</summary>
    public byte RebornCount { get; set; }

    /// <summary>The character's job.</summary>
    public byte Job { get; set; }

    /// <summary>The maximum HP.</summary>
    public int MaxHp { get; set; } = 100;

    /// <summary>The current HP.</summary>
    public int Hp { get; set; } = 100;

    /// <summary>The maximum SP.</summary>
    public int MaxSp { get; set; } = 100;

    /// <summary>The current SP.</summary>
    public int Sp { get; set; } = 100;

    /// <summary>The total experience.</summary>
    public int Exp { get; set; }

    /// <summary>The gold carried.</summary>
    public int Gold { get; set; }

    /// <summary>The id of the map the character is on.</summary>
    public ushort MapId { get; set; }

    /// <summary>The x position on the map.</summary>
    public ushort X { get; set; }

    /// <summary>The y position on the map.</summary>
    public ushort Y { get; set; }

    /// <summary>The worn items.</summary>
    public List<EquippedItem> Equipment { get; set; } = [];

    /// <summary>The bag stacks, slots 1 to 50.</summary>
    public List<BagItem> Bag { get; set; } = [];

    /// <summary>The stacks in the bank.</summary>
    public List<BagItem> Bank { get; set; } = [];

    /// <summary>The gold in the bank.</summary>
    public int BankGold { get; set; }

    /// <summary>The stacks in the warehouse.</summary>
    public List<BagItem> Warehouse { get; set; } = [];

    /// <summary>The stacks in the house storeroom.</summary>
    public List<BagItem> Storeroom { get; set; } = [];

    /// <summary>The stacks in the Item Bag.</summary>
    public List<BagItem> MiniGrid { get; set; } = [];

    /// <summary>The parts covered by the space contract, as flag bits (stock keeper, hotel, tent, garage).</summary>
    public byte SpaceFlags { get; set; }

    /// <summary>When the space contract ends (UTC); null when there is none.</summary>
    public DateTime? SpaceUntilUtc { get; set; }

    /// <summary>The item id of the floor covering of the house; 0 for none.</summary>
    public ushort HouseFloor { get; set; }

    /// <summary>The state bytes of the house pillars on the ground floor.</summary>
    public byte[] PillarsGround { get; set; } = new byte[48];

    /// <summary>The state bytes of the house pillars on the upper floor.</summary>
    public byte[] PillarsUpper { get; set; } = new byte[48];

    /// <summary>The fish tank, one byte per fish place; a byte that is not 0 is a fish bought for that place.</summary>
    public byte[] FishTank { get; set; } = new byte[8];

    /// <summary>The notes on the house blackboard.</summary>
    public List<BoardNote> Blackboard { get; set; } = [];

    /// <summary>The item ids of the books in the book case, ten places.</summary>
    public ushort[] BookCase { get; set; } = new ushort[10];

    /// <summary>The item ids of the books on the book desk, ten places.</summary>
    public ushort[] BookDesk { get; set; } = new ushort[10];

    /// <summary>The experience stored on the puppet memory card.</summary>
    public int MemCardExp { get; set; }

    /// <summary>The upgrade step of the puppet memory card.</summary>
    public byte MemCardStep { get; set; }

    /// <summary>The player id of the helper whose puppet play is running for this character; 0 for none.</summary>
    public int PuppetHelperId { get; set; }

    /// <summary>The bag slot of the item that holds the running puppet play.</summary>
    public byte PuppetBagSlot { get; set; }

    /// <summary>When the running puppet play started.</summary>
    public DateTime PuppetSince { get; set; }

    /// <summary>The puppet cards this character holds as a helper.</summary>
    public List<PuppetCard> PuppetCards { get; set; } = [];

    /// <summary>The three GPS marks of three values each, 9 values in all; a mark whose first value is 0 is empty.</summary>
    public ushort[] GpsMarks { get; set; } = new ushort[9];

    /// <summary>The gas in the large house gas tank.</summary>
    public ushort GasCan { get; set; }

    /// <summary>The gas in the small house gas tank.</summary>
    public ushort GasSmall { get; set; }

    /// <summary>The fish food left for the fish tank.</summary>
    public byte FishFood { get; set; }

    /// <summary>The item id of the wall covering of the house; 0 for none.</summary>
    public ushort HouseWall { get; set; }

    /// <summary>The gold held at the ATM.</summary>
    public int AtmGold { get; set; }

    /// <summary>The map of the character's saved record point; 0 for none.</summary>
    public ushort RecordMapId { get; set; }

    /// <summary>The service number stored with the record point by the event that saved it.</summary>
    public ushort RecordPoint { get; set; }

    /// <summary>The x position of the record point.</summary>
    public ushort RecordX { get; set; }

    /// <summary>The y position of the record point.</summary>
    public ushort RecordY { get; set; }

    /// <summary>True once the starter kit was given.</summary>
    public bool StarterKitGiven { get; set; }

    /// <summary>Lists the worn items' ids in slot order.</summary>
    /// <returns>The item ids.</returns>
    public IEnumerable<ushort> EquippedItemIds() => Equipment.OrderBy(e => e.Slot).Select(e => e.ItemId);

    /// <summary>The monster book entries.</summary>
    public List<ushort> MonsterBook { get; set; } = [];

    /// <summary>The day the online draw count below belongs to.</summary>
    public int OnlineDrawDay { get; set; }

    /// <summary>How many online draws were used on that day.</summary>
    public byte OnlineDrawsUsed { get; set; }

    /// <summary>The quest marks with a count.</summary>
    public List<QuestMark> Marks { get; set; } = [];

    /// <summary>The ids of the quest marks that are flags and are set.</summary>
    public List<ushort> MarkFlags { get; set; } = [];

    /// <summary>The week of the last reset of the weekly activity flags.</summary>
    public int ActivityWeek { get; set; }

    /// <summary>Gives the count of a quest mark.</summary>
    /// <param name="id">The mark id.</param>
    /// <returns>The mark's count, 1 for a set flag mark, otherwise 0.</returns>
    public int MarkCount(ushort id) => Marks.Find(m => m.Id == id)?.Count ?? (MarkFlags.Contains(id) ? 1 : 0);

    /// <summary>The cooldowns game scripts started for the character.</summary>
    public List<ScriptCooldown> ScriptCooldowns { get; set; } = [];

    /// <summary>Tells whether a script cooldown is still running.</summary>
    /// <param name="id">The cooldown id.</param>
    /// <param name="now">The current time (UTC).</param>
    /// <returns>True while the cooldown runs.</returns>
    public bool CooldownRunning(ushort id, DateTime now) => ScriptCooldowns.Exists(c => c.Id == id && c.UntilUtc > now);

    /// <summary>Starts a script cooldown, replacing one with the same id and dropping those that have ended.</summary>
    /// <param name="id">The cooldown id.</param>
    /// <param name="now">The current time (UTC).</param>
    /// <param name="seconds">How long it runs.</param>
    public void StartCooldown(ushort id, DateTime now, int seconds)
    {
        ScriptCooldowns.RemoveAll(c => c.Id == id || c.UntilUtc <= now);
        ScriptCooldowns.Add(new ScriptCooldown { Id = id, UntilUtc = now.AddSeconds(seconds) });
    }

    /// <summary>The timed effects running on the character.</summary>
    public List<TimedEffect> Effects { get; set; } = [];

    /// <summary>Since when the experience gain is paused by the Stopwatch; null when it is not paused.</summary>
    public DateTime? ExpPausedSince { get; set; }

    /// <summary>The Npc.dat id of the Riceball shape the character took; 0 for none.</summary>
    public ushort BodyShiftNpc { get; set; }

    /// <summary>True while the character wears the Riceball shape.</summary>
    public bool BodyShiftOn { get; set; }

    /// <summary>When the shape runs out (UTC) while it is worn.</summary>
    public DateTime BodyShiftEnds { get; set; }

    /// <summary>The seconds left of the shape while it is not worn.</summary>
    public int BodyShiftLeft { get; set; }

    /// <summary>The skills the character has learned.</summary>
    public List<LearnedSkill> Skills { get; set; } = [];

    /// <summary>Finds a learned skill.</summary>
    /// <param name="id">The skill id.</param>
    /// <returns>The skill, or null when it is not learned.</returns>
    public LearnedSkill? Skill(ushort id) => Skills.Find(s => s.Id == id);

    /// <summary>The pets the character carries.</summary>
    public List<Pet> Pets { get; set; } = [];

    /// <summary>The pets left in the inn's pet storage.</summary>
    public List<Pet> StoredPets { get; set; } = [];

    /// <summary>The team NPCs that left the team, kept so that they come back as they were.</summary>
    public List<Pet> DepartedTeamNpcs { get; set; } = [];

    /// <summary>True while a sandglass is armed for the next manufacture.</summary>
    public bool SandglassArmed { get; set; }

    /// <summary>The training progress in percent; reset to 0 when the level changes.</summary>
    public byte TrainPercent { get; set; }

    /// <summary>The level at which the training progress was last updated.</summary>
    public byte TrainLevel { get; set; }

    /// <summary>The pet ids already given by the reborn wish.</summary>
    public List<ushort> RebornWishes { get; set; } = [];

    /// <summary>The Npc.dat id of the pet that joins battles; 0 for none.</summary>
    public ushort ActivePetId { get; set; }

    /// <summary>The slot of the pet the character rides, kept over a logout; 0 for none.</summary>
    public byte RidingPetSlot { get; set; }

    /// <summary>The friend list.</summary>
    public List<FriendEntry> Friends { get; set; } = [];

    /// <summary>The number of PvP battles won.</summary>
    public ushort PvpWins { get; set; }

    /// <summary>The online status the player chose; not saved.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public byte IcqStatus { get; set; }

    /// <summary>The PvP rank.</summary>
    public byte PvpRank { get; set; }

    /// <summary>The HP stored in the HP bottle.</summary>
    public int HpBottle { get; set; }

    /// <summary>The SP stored in the SP bottle.</summary>
    public int SpBottle { get; set; }

    /// <summary>The stall cases.</summary>
    public List<StallCase> StallCases { get; set; } = [];

    /// <summary>The fuel stored in the fuel bank, one row per item.</summary>
    public List<FuelRow> FuelTank { get; set; } = [];

    /// <summary>The vehicles parked in the garage.</summary>
    public List<ParkedVehicle> Parking { get; set; } = [];

    /// <summary>The furniture mails.</summary>
    public List<FurMail> FurMails { get; set; } = [];

    /// <summary>The cells of the picnic box.</summary>
    public List<BoxSlot> PicnicSlots { get; set; } = [];

    /// <summary>The cells of the experience bag.</summary>
    public List<BoxSlot> ExpBagSlots { get; set; } = [];

    /// <summary>The blocklist.</summary>
    public List<BlockedPlayer> Blocked { get; set; } = [];

    /// <summary>The Lucky 7 plays made in the New Year event since the last free one.</summary>
    public int NewYearLucky7 { get; set; }

    /// <summary>The mails received.</summary>
    public List<Mail> Mailbox { get; set; } = [];

    /// <summary>The hotkey bar entries.</summary>
    public List<Hotkey> Hotkeys { get; set; } = [];

    /// <summary>The character's nickname.</summary>
    public string Nickname { get; set; } = "";

    /// <summary>The heart number in the profile.</summary>
    public byte HeartNumber { get; set; }

    /// <summary>The blood type in the profile.</summary>
    public byte BloodType { get; set; }

    /// <summary>The birth year in the profile.</summary>
    public byte BirthYear { get; set; }

    /// <summary>The birth month in the profile.</summary>
    public byte BirthMonth { get; set; }

    /// <summary>The birth day in the profile.</summary>
    public byte BirthDay { get; set; }

    /// <summary>The first constellation in the profile.</summary>
    public byte Constellation1 { get; set; }

    /// <summary>The second constellation in the profile.</summary>
    public byte Constellation2 { get; set; }

    /// <summary>The system option set by <c>33/1</c>, sent back at login.</summary>
    public byte Option1 { get; set; }

    /// <summary>The system option set by <c>33/2</c>, sent back at login.</summary>
    public byte Option2 { get; set; }

    /// <summary>The system option set by <c>33/6</c>, sent back at login.</summary>
    public byte Option6 { get; set; }

    /// <summary>The party option set by <c>33/5</c>; 0 means the default, 1.</summary>
    public byte PartyOption { get; set; }

    /// <summary>The chat channels switched on, one bit each; all five on by default.</summary>
    public byte ChannelBits { get; set; } = 0x1F;

    /// <summary>The week the castle reward was last taken.</summary>
    public int CastleRewardWeek { get; set; }

    /// <summary>The tent the character has pitched; null for none.</summary>
    public PitchedTent? Tent { get; set; }

    /// <summary>The first kind value of the character's tent.</summary>
    public byte TentKind1 { get; set; }

    /// <summary>The second kind value of the character's tent.</summary>
    public byte TentKind2 { get; set; }

    /// <summary>The furniture in the house and the tent.</summary>
    public List<Furniture> Furniture { get; set; } = [];

    /// <summary>The points collected at the hot spring.</summary>
    public int SpringBonus { get; set; }

    /// <summary>The items kept in the box furniture of the house.</summary>
    public List<BoxItem> HouseBoxes { get; set; } = [];

    /// <summary>The index of the house background music.</summary>
    public int HouseBgmIndex { get; set; }

    /// <summary>The file of the house background music; null for none.</summary>
    public string? HouseBgmFile { get; set; }

    /// <summary>The text on the house doorplate; null for none.</summary>
    public string? Doorplate { get; set; }

    /// <summary>True once the house has been furnished.</summary>
    public bool HouseFurnished { get; set; }

    /// <summary>The manufacture jobs under way.</summary>
    public List<MakeJob> MakeJobs { get; set; } = [];

    /// <summary>The day the character last entered an instance.</summary>
    public string InstanceDay { get; set; } = "";

    /// <summary>The map the character returns to when leaving an instance.</summary>
    public ushort InstanceReturnMapId { get; set; }

    /// <summary>The x position the character returns to when leaving an instance.</summary>
    public ushort InstanceReturnX { get; set; }

    /// <summary>The y position the character returns to when leaving an instance.</summary>
    public ushort InstanceReturnY { get; set; }

    /// <summary>The map the character came to Carnie from, where the Carnie exit sends it back; 0 for none.</summary>
    public ushort CarnieReturnMapId { get; set; }

    /// <summary>The x position the character came to Carnie from.</summary>
    public ushort CarnieReturnX { get; set; }

    /// <summary>The y position the character came to Carnie from.</summary>
    public ushort CarnieReturnY { get; set; }
}
