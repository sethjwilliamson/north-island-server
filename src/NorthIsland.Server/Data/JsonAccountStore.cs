using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NorthIsland.Server.Data;

/// <summary>
/// Account store that keeps one JSON file per account under <c>accounts</c> in the data directory and all accounts in memory.
/// </summary>
public sealed class JsonAccountStore : IAccountStore, IDisposable
{
    /// <summary>The first login id given to a new account.</summary>
    public const int FirstLoginId = 5000;

    /// <summary>Options for reading and writing the account files (indented).</summary>
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>The folder holding the account files.</summary>
    private readonly string _directory;

    /// <summary>Logs account creation and file problems.</summary>
    private readonly ILogger<JsonAccountStore> _logger;

    /// <summary>Serialises all access to the accounts and their files.</summary>
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>All accounts, by name, ignoring case.</summary>
    private readonly Dictionary<string, Account> _byName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The login id the next new account gets.</summary>
    private int _nextLoginId = FirstLoginId;

    /// <summary>Loads every account file of the data directory, creating the folder if it is missing.</summary>
    /// <param name="dataDirectory">The server data directory; the account files are in its <c>accounts</c> folder.</param>
    /// <param name="logger">Logs how many accounts were loaded.</param>
    public JsonAccountStore(
        string dataDirectory,
        ILogger<JsonAccountStore> logger)
    {
        _directory = Path.Combine(dataDirectory, "accounts");
        _logger = logger;
        Directory.CreateDirectory(_directory);

        foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            var account = JsonSerializer.Deserialize<Account>(File.ReadAllText(file), JsonOptions)
                ?? throw new InvalidDataException($"Empty account file {file}");
            _byName.Add(account.Name, account);
            _nextLoginId = Math.Max(_nextLoginId, account.LoginId + 1);
        }
        _logger.LogInformation("Loaded {Count} account(s) from {Directory}", _byName.Count, Path.GetFullPath(_directory));
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
                LoginId = _nextLoginId++,
                Name = name,
                PasswordHash = hash,
                CreatedUtc = GameClock.UtcNow,
            };
            _byName.Add(name, account);
            await WriteAsync(account, ct).ConfigureAwait(false);
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

    /// <summary>
    /// Writes an account to its file by writing a temporary file and renaming it over the old one, retrying while the rename is blocked.
    /// </summary>
    /// <param name="account">The account to write.</param>
    /// <param name="ct">Cancels the write and the retry delays.</param>
    private async Task WriteAsync(Account account, CancellationToken ct)
    {
        var path = Path.Combine(_directory, account.Name.ToLowerInvariant() + ".json");
        var temp = path + ".tmp";
        await using (var stream = File.Create(temp))
            await JsonSerializer.SerializeAsync(stream, account, JsonOptions, ct).ConfigureAwait(false);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException && attempt < 20)
            {
                _logger.LogDebug("Rename of {Path} failed (attempt {Attempt}): {Message}", path, attempt, ex.Message);
                await Task.Delay(50, ct).ConfigureAwait(false);
            }
        }
    }
}
