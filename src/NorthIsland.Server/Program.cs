using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// The settings files sit next to the program, wherever it is started from.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });

SettingsFiles.AddTo(builder.Configuration);

var gameDataKey = $"{ServerOptions.Section}:{nameof(ServerOptions.GameDataDirectory)}";

builder.Configuration.AddCommandLine(args, new Dictionary<string, string> { ["--data"] = gameDataKey });

var gameDataPlaces = GameDataFolder.Places(builder.Configuration[gameDataKey]);

var gameData = GameDataFolder.Find(gameDataPlaces);

if (gameData is null)
{
    await Console.Error.WriteLineAsync(
        $"No game data found. Looked in: {string.Join("; ", gameDataPlaces)}. Start with --data <folder of the game> or set Server:GameDataDirectory.")
        .ConfigureAwait(false);
    return 1;
}

builder.Configuration.AddInMemoryCollection([new(gameDataKey, gameData)]);

builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss.fff ";
});

// The console escapes control characters in messages as the log file does: text a client sent must not reach the terminal raw.
PrintableLoggerProvider.WrapConsole(builder.Logging);

var serverSection = builder.Configuration.GetSection(ServerOptions.Section);

var logDirectory = ServerPaths.Resolve(serverSection[nameof(ServerOptions.LogDirectory)] ?? "logs");

builder.Logging.AddProvider(new FileLoggerProvider(
    logDirectory,
    serverSection.GetValue(nameof(ServerOptions.LogFileMegabytes), FileLoggerProvider.DefaultFileMegabytes),
    serverSection.GetValue(nameof(ServerOptions.LogFilesPerDay), FileLoggerProvider.DefaultFilesPerDay)));

builder.Services.AddGameServer(serverSection);

await builder.Build().RunAsync().ConfigureAwait(false);

return 0;
