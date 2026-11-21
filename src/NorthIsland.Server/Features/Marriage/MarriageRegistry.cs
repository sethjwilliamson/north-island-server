using Microsoft.Extensions.Logging;
using System.Collections.Immutable;

namespace NorthIsland.Server.Features.Marriage;

/// <summary>Keeps the couples, applications and divorces, saved as a document, and the in-memory sets of ceremonies and hidden pets.</summary>
public sealed class MarriageRegistry
{
    /// <summary>Lowest level to apply; a reborn player may apply at any level.</summary>
    public const int MinLevel = 30;

    /// <summary>Gold each partner pays when the application is made.</summary>
    public const int ApplicationFee = 60_000;

    /// <summary>Gold the divorce applicant pays.</summary>
    public const int DivorceFee = 200_000;

    /// <summary>How long an application stays valid.</summary>
    public static readonly TimeSpan ApplicationTime = TimeSpan.FromDays(3);

    /// <summary>How long after a divorce a player has to wait before applying again.</summary>
    public static readonly TimeSpan RemarryWait = TimeSpan.FromDays(7);

    /// <summary>How long a spouse must have gone unseen before the partner may divorce without their answer.</summary>
    public static readonly TimeSpan AwayForDivorce = TimeSpan.FromDays(30);

    /// <summary>Item id of the groom's ring.</summary>
    public const ushort GroomRing = 25610; // Diamond Band

    /// <summary>Item id of the bride's ring.</summary>
    public const ushort BrideRing = 25609; // Wedding Band

    /// <summary>Item ids of the wedding clothes of the groom.</summary>
    public static readonly ImmutableArray<ushort> GroomClothes = [21207, 24164]; // 21207 Groom's Wear, 24164 Groom's Boots

    /// <summary>Item ids of the wedding clothes of the bride.</summary>
    public static readonly ImmutableArray<ushort> BrideClothes = [21208, // Bride's Wear
        24165, // Bride's Boots
        22211, // Bride's Veil
        18070]; // Bouquets

    /// <summary>Mark set on both players while their application is in force; cleared when they marry.</summary>
    public const ushort AppliedFlag = 11045; // Applied to enter the wedding venue

    /// <summary>Mark set on a married player; cleared on divorce.</summary>
    public const ushort MarriedFlag = 11047; // Reserved

    /// <summary>Login ids of the players who are in a wedding ceremony.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> _inCeremony = new();

    /// <summary>Marks both players as being in a ceremony.</summary>
    /// <param name="groomId">Login id of the groom.</param>
    /// <param name="brideId">Login id of the bride.</param>
    public void BeginCeremony(int groomId, int brideId) { _inCeremony[groomId] = 1; _inCeremony[brideId] = 1; }

    /// <summary>Clears the ceremony mark of both players.</summary>
    /// <param name="groomId">Login id of the groom.</param>
    /// <param name="brideId">Login id of the bride.</param>
    public void EndCeremony(int groomId, int brideId) { _inCeremony.TryRemove(groomId, out _); _inCeremony.TryRemove(brideId, out _); }

    /// <summary>Tells whether a player is in a ceremony.</summary>
    /// <param name="playerId">The player's login id.</param>
    /// <returns>True when the player is in a ceremony.</returns>
    public bool InCeremony(int playerId) => _inCeremony.ContainsKey(playerId);

    /// <summary>Login ids of the players whose pets are hidden for a ceremony.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> _petsHidden = new();

    /// <summary>Marks a player's pets as hidden.</summary>
    /// <param name="playerId">The player's login id.</param>
    /// <returns>True when the pets were not hidden before.</returns>
    public bool HidePets(int playerId) => _petsHidden.TryAdd(playerId, 1);

    /// <summary>Marks a player's pets as shown again.</summary>
    /// <param name="playerId">The player's login id.</param>
    /// <returns>True when the pets were hidden.</returns>
    public bool ShowPets(int playerId) => _petsHidden.TryRemove(playerId, out _);

    /// <summary>Name of the stored document.</summary>
    private const string Document = "marriages";

    /// <summary>The store the marriage book is loaded from and saved to.</summary>
    private readonly IDocumentStore _documents;

    /// <summary>Logger.</summary>
    private readonly ILogger<MarriageRegistry> _logger;

    /// <summary>The marriage data.</summary>
    private readonly MarriageBook _book;

    /// <summary>Loads the marriage book from the document store, or starts an empty one.</summary>
    /// <param name="documents">The document store.</param>
    /// <param name="logger">Logger.</param>
    public MarriageRegistry(
        IDocumentStore documents,
        ILogger<MarriageRegistry> logger)
    {
        _logger = logger;
        _documents = documents;
        _book = documents.Load<MarriageBook>(Document) ?? new();
    }

    /// <summary>Lock that guards the marriage book and the partner cards.</summary>
    public Lock Lock { get; } = new();

    /// <summary>Tells whether a body type counts as male.</summary>
    /// <param name="bodyType">The character's body type.</param>
    /// <returns>True for body types 1 and 3.</returns>
    public static bool IsMale(byte bodyType) => bodyType is 1 or 3;

    /// <summary>Finds the couple a player belongs to.</summary>
    /// <param name="id">The player's login id.</param>
    /// <returns>The couple, or null when the player is not married.</returns>
    public Couple? CoupleOf(int id)
    {
        lock (Lock)
            return _book.Couples.Find(c => c.Has(id));
    }

    /// <summary>Finds the player's application, dropping expired ones first.</summary>
    /// <param name="id">The player's login id.</param>
    /// <param name="now">The current time (UTC).</param>
    /// <returns>The application, or null when the player has none.</returns>
    public MarriageApplication? ApplicationOf(int id, DateTime now)
    {
        lock (Lock)
        {
            _book.Applications.RemoveAll(a => a.UntilUtc <= now);
            return _book.Applications.Find(a => a.Has(id));
        }
    }

    /// <summary>Tells whether a player is still in the wait after a divorce.</summary>
    /// <param name="id">The player's login id.</param>
    /// <param name="now">The current time (UTC).</param>
    /// <returns>True when the wait has not passed.</returns>
    public bool Cooling(int id, DateTime now)
    {
        lock (Lock)
            return _book.DivorcedUtc.TryGetValue(id, out var when) && now - when < RemarryWait;
    }

    /// <summary>Records an application, replacing any application of either player, and saves.</summary>
    /// <param name="groomId">Login id of the groom.</param>
    /// <param name="brideId">Login id of the bride.</param>
    /// <param name="now">The current time (UTC); the deadline is counted from it.</param>
    /// <returns>The new application.</returns>
    public MarriageApplication Apply(int groomId, int brideId, DateTime now)
    {
        var application = new MarriageApplication { GroomId = groomId, BrideId = brideId, UntilUtc = now + ApplicationTime };
        lock (Lock)
        {
            _book.Applications.RemoveAll(a => a.Has(groomId) || a.Has(brideId));
            _book.Applications.Add(application);
        }
        Save();
        return application;
    }

    /// <summary>
    /// Turns an application into a couple, refreshing both cards from the characters, and saves; refused when the application is no longer in the
    /// book (expired or replaced) or either partner is already married.
    /// </summary>
    /// <param name="application">The application to complete.</param>
    /// <param name="groom">The groom's character.</param>
    /// <param name="bride">The bride's character.</param>
    /// <returns>The new couple, or null when the marriage was refused.</returns>
    public Couple? Marry(MarriageApplication application, Character groom, Character bride)
    {
        var couple = new Couple
        {
            Husband = new SpouseCard { Id = application.GroomId },
            Wife = new SpouseCard { Id = application.BrideId },
            Married = GameClock.Now.ToOADate(),
        };
        couple.Husband.Refresh(groom);
        couple.Wife.Refresh(bride);
        lock (Lock)
        {
            if (!_book.Applications.Contains(application) || _book.Couples.Exists(c => c.Has(application.GroomId) || c.Has(application.BrideId)))
                return null;
            _book.Applications.Remove(application);
            _book.Couples.Add(couple);
        }
        Save();
        return couple;
    }

    /// <summary>Ends a marriage, notes the divorce time of both partners and saves.</summary>
    /// <param name="couple">The couple to divorce.</param>
    /// <param name="now">The current time (UTC).</param>
    /// <param name="absentId">Login id of a partner who is offline and must be told at the next login, or 0.</param>
    public void Divorce(Couple couple, DateTime now, int absentId = 0)
    {
        lock (Lock)
        {
            _book.Couples.Remove(couple);
            _book.DivorcedUtc[couple.Husband.Id] = now;
            _book.DivorcedUtc[couple.Wife.Id] = now;
            if (absentId != 0)
                _book.DivorcedWhileAway.Add(absentId);
        }
        Save();
    }

    /// <summary>Removes a player from the list of those divorced while offline, and saves if they were listed.</summary>
    /// <param name="id">The player's login id.</param>
    /// <returns>True when the player was on the list.</returns>
    public bool TakeDivorcedWhileAway(int id)
    {
        bool taken;
        lock (Lock)
            taken = _book.DivorcedWhileAway.Remove(id);
        if (taken)
            Save();
        return taken;
    }

    /// <summary>Updates a player's card from their character and saves; does nothing for a player who is not married.</summary>
    /// <param name="id">The player's login id.</param>
    /// <param name="character">The player's character.</param>
    public void Refresh(int id, Character character)
    {
        if (CoupleOf(id) is not { } couple)
            return;
        lock (Lock)
            couple.CardOf(id).Refresh(character);
        Save();
    }

    /// <summary>Writes the marriage book to the document store; a failed write is logged.</summary>
    public void Save()
    {
        lock (Lock)
        {
            try
            {
                _documents.Save(Document, _book);
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "Saving {Document} failed", Document);
            }
        }
    }
}
