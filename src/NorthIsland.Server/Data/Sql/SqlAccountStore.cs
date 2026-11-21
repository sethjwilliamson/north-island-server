using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NorthIsland.Server.Data.Sql;

/// <summary>
/// Account store backed by the database; all accounts are also held in memory, and each save writes the account's row and its character rows.
/// </summary>
public sealed class SqlAccountStore : IAccountStore, IDisposable
{
    /// <summary>Options for the account JSON stored in the <c>Data</c> column.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new();

    /// <summary>The options for every database context this store opens.</summary>
    private readonly DbContextOptions<ServerDbContext> _options;

    /// <summary>Logs account creation and the import.</summary>
    private readonly ILogger<SqlAccountStore> _logger;

    /// <summary>Serialises all access to the accounts and the database writes.</summary>
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>All accounts, by name, ignoring case.</summary>
    private readonly Dictionary<string, Account> _byName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The login id the next new account gets.</summary>
    private int _nextLoginId = JsonAccountStore.FirstLoginId;

    /// <summary>The options for opening a database context on the store's database.</summary>
    public DbContextOptions<ServerDbContext> Options => _options;

    /// <summary>Prepares the database schema, optionally imports the JSON account files, and loads every account.</summary>
    /// <param name="options">The context options, naming the database.</param>
    /// <param name="logger">Logs how many accounts were loaded.</param>
    /// <param name="migrate">True to apply the EF Core migrations; false to create the schema directly.</param>
    /// <param name="importDirectory">A data directory whose <c>accounts</c> folder is imported when the database has no accounts, or null.</param>
    public SqlAccountStore(
        DbContextOptions<ServerDbContext> options,
        ILogger<SqlAccountStore> logger,
        bool migrate,
        string? importDirectory)
    {
        _options = options;
        _logger = logger;

        using (var db = new ServerDbContext(options))
        {
            if (migrate)
                db.Database.Migrate();
            else
                db.Database.EnsureCreated();
        }
        if (importDirectory is not null)
            ImportJsonFiles(importDirectory);

        using (var db = new ServerDbContext(options))
        {
            foreach (var data in db.Accounts.AsNoTracking().Select(a => a.Data).ToList())
            {
                var account = JsonSerializer.Deserialize<Account>(data, JsonOptions) ?? throw new InvalidDataException("Empty account document");
                _byName.Add(account.Name, account);
                _nextLoginId = Math.Max(_nextLoginId, account.LoginId + 1);
            }
        }
        _logger.LogInformation("Loaded {Count} account(s) from the database", _byName.Count);
    }

    public async Task<Account?> FindByNameAsync(string name, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _byName.GetValueOrDefault(name);
        }
        finally
        {
            _lock.Release();
        }
    }

    public IReadOnlyList<Account> All()
    {
        _lock.Wait();
        try
        {
            return [.. _byName.Values];
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<Account?> FindByLoginIdAsync(int loginId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _byName.Values.FirstOrDefault(a => a.LoginId == loginId);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<Account> CreateAsync(string name, string password, CancellationToken ct = default)
    {
        var hash = PasswordHasher.Hash(password); // hashed before the lock: the key stretching would hold up every other account access
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_byName.ContainsKey(name))
                throw new InvalidOperationException($"Account '{name}' already exists.");

            var account = new Account
            {
                LoginId = _nextLoginId,
                Name = name,
                PasswordHash = hash,
                CreatedUtc = GameClock.UtcNow,
            };
            await WriteAsync(account, ct).ConfigureAwait(false);
            _nextLoginId++;
            _byName.Add(name, account);
            _logger.LogInformation("Created account {Name} with login id {LoginId}", name, account.LoginId);
            return account;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(Account account, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WriteAsync(account, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> IsCharacterNameTakenAsync(string characterName, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _byName.Values.Any(a => a.Characters.Exists(c => string.Equals(c.Name, characterName, StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose() => _lock.Dispose();

    /// <summary>Inserts or updates the account's row and its character rows.</summary>
    /// <param name="account">The account to write.</param>
    /// <param name="ct">Cancels the write.</param>
    private async Task WriteAsync(Account account, CancellationToken ct)
    {
        await using var db = new ServerDbContext(_options);
        var row = await db.Accounts.Include(a => a.Characters).SingleOrDefaultAsync(a => a.LoginId == account.LoginId, ct).ConfigureAwait(false);
        if (row is null)
        {
            row = new AccountRow { LoginId = account.LoginId };
            db.Accounts.Add(row);
        }
        Fill(row, account);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Copies an account into its row, including the JSON and the character rows (added, updated or removed to match).
    /// </summary>
    /// <param name="row">The row to fill.</param>
    /// <param name="account">The account to copy from.</param>
    private static void Fill(AccountRow row, Account account)
    {
        row.Name = account.Name;
        row.NameKey = account.Name.ToLowerInvariant();
        row.CreatedUtc = Utc(account.CreatedUtc);
        row.LastLoginUtc = account.LastLoginUtc is { } last ? Utc(last) : null;
        row.OnlineSeconds = account.OnlineSeconds;
        row.MallPoints = account.MallPoints;
        row.SavedUtc = GameClock.UtcNow;
        row.Data = JsonSerializer.Serialize(account, JsonOptions);

        row.Characters.RemoveAll(r => !account.Characters.Exists(c => c.Slot == r.Slot));
        foreach (var character in account.Characters)
        {
            var c = row.Characters.Find(r => r.Slot == character.Slot);
            if (c is null)
            {
                c = new CharacterRow { LoginId = account.LoginId, Slot = character.Slot };
                row.Characters.Add(c);
            }
            c.Name = character.Name;
            c.NameKey = character.Name.ToLowerInvariant();
            c.Level = character.Level;
            c.MapId = character.MapId;
        }
    }

    /// <summary>Marks a time as UTC without converting it, unless it already is.</summary>
    /// <param name="time">The time to mark.</param>
    /// <returns>The same time with kind UTC.</returns>
    private static DateTime Utc(DateTime time) => time.Kind == DateTimeKind.Utc ? time : DateTime.SpecifyKind(time, DateTimeKind.Utc);

    /// <summary>
    /// Imports the account files of a data directory into the database; does nothing when the database already has accounts.
    /// </summary>
    /// <param name="dataDirectory">The data directory with an <c>accounts</c> folder.</param>
    private void ImportJsonFiles(string dataDirectory)
    {
        using (var db = new ServerDbContext(_options))
        {
            if (db.Accounts.Any() || !Directory.Exists(Path.Combine(dataDirectory, "accounts")))
                return;
        }

        using var files = new JsonAccountStore(dataDirectory, NullLogger<JsonAccountStore>.Instance);
        var accounts = files.All();
        if (accounts.Count == 0)
            return;
        using (var db = new ServerDbContext(_options))
        {
            foreach (var account in accounts)
            {
                var row = new AccountRow { LoginId = account.LoginId };
                Fill(row, account);
                db.Accounts.Add(row);
            }
            db.SaveChanges();
        }
        _logger.LogInformation("Imported {Count} account file(s) from {Directory} into the empty database", accounts.Count,
            Path.GetFullPath(Path.Combine(dataDirectory, "accounts")));
    }
}
