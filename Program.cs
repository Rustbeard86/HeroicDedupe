using HeroicDedupe.Models;
using HeroicDedupe.Readers;
using HeroicDedupe.Services;
using Microsoft.Extensions.Configuration;

// Top-level statements for .NET 10 modern entry point
var logger = ConsoleLogger.Instance;

logger.WriteLine("=== Heroic Library Deduplicator (v1.0) ===");

// 1. Load Configuration
var config = LoadConfiguration(args);

if (!ValidateConfiguration(config, logger))
    return 1;

ResolvePaths(config);

logger.WriteLine($"Mode: {(config.DryRun ? "[TEST] DRY RUN (Safe Mode)" : "[LIVE] (Write Mode)")}");
logger.WriteLine($"Priority: {string.Join(" > ", config.GetPriorityOrDefault())}");

// 2. Check Process State (only needed for live mode - dry run doesn't write to disk)
HeroicProcessManager? processManager = null;
if (!config.DryRun)
{
    processManager = new HeroicProcessManager(logger);
    if (processManager.IsHeroicRunning() && !processManager.RequestClose())
    {
        logger.WriteLine("Exiting to prevent data corruption.");
        return 1;
    }
}

// 3. Read Libraries
List<IGameStoreReader> readers =
[
    new LegendaryReader(config.LegendaryLibraryPath, logger),
    new GogReader(config.GogLibraryPath, logger),
    new NileReader(config.NileLibraryPath, logger)
];

List<LocalGame> allGames = [];

foreach (var reader in readers)
{
    logger.Write($"Reading {reader.StoreType,-8}... ");
    var games = await reader.ReadLibraryAsync();

    if (games.Count > 0)
        logger.WriteLine($"{games.Count} games found.", ConsoleColor.Cyan);
    else
        logger.WriteLine("None / File missing.", ConsoleColor.DarkGray);

    allGames.AddRange(games);
}

// 4. Optional: Enrich with IGDB metadata
allGames = await EnrichWithMetadataAsync(allGames, config, logger);

// 5. Deduplicate
var deduper = new DeduplicationService(config, logger);
var gamesToHide = deduper.IdentifyDuplicates(allGames);

logger.WriteLine($"\nTotal Duplicates found to hide: {gamesToHide.Count}");

// 6. Apply Changes
if (gamesToHide.Count > 0)
{
    var modifier = new HeroicConfigModifier(config, logger);
    try
    {
        await modifier.ApplyHiddenGamesAsync(gamesToHide);
    }
    catch (Exception ex)
    {
        logger.WriteLine($"CRITICAL ERROR: {ex.Message}", ConsoleColor.Red);
        return 1;
    }
}
else
{
    logger.WriteLine("Your library is already clean!");
}

// 7. Restart Heroic if we closed it
processManager?.RestartHeroic();

logger.WriteLine("\nPress any key to exit.");
Console.ReadKey();
return 0;

// --- HELPER FUNCTIONS ---

static async Task<List<LocalGame>> EnrichWithMetadataAsync(List<LocalGame> games, AppConfig config,
    IConsoleLogger logger)
{
    // Skip if IGDB not configured
    if (config.Igdb is not { IsConfigured: true })
    {
        logger.WriteLine("[IGDB] Not configured - using local metadata only.", ConsoleColor.DarkGray);
        return games;
    }

    // Create cache and provider with dependency injection
    var cachePath = Path.Combine(AppContext.BaseDirectory, "igdb_cache.json");
    var cache = new FileReleaseDateCache(cachePath, logger);

    if (config.RefreshCache)
    {
        logger.WriteLine("[IGDB] --refresh-cache: clearing cached metadata...", ConsoleColor.Cyan);
        cache.Clear();
    }

    using var provider = new IgdbService(config.Igdb, cache, logger);
    return await provider.EnrichGamesAsync(games);
}

static AppConfig LoadConfiguration(string[] args)
{
    var builder = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", false, false)
        .AddCommandLine(args);

    var cfg = new AppConfig();
    builder.Build().Bind(cfg);

    // Handle CLI overrides
    foreach (var arg in args)
        if (arg.Equals("--dry-run", StringComparison.OrdinalIgnoreCase))
            cfg.DryRun = true;
        else if (arg.Equals("--live", StringComparison.OrdinalIgnoreCase))
            cfg.DryRun = false;
        else if (arg.Equals("--refresh-cache", StringComparison.OrdinalIgnoreCase))
            cfg.RefreshCache = true;

    return cfg;
}

static bool ValidateConfiguration(AppConfig config, IConsoleLogger logger)
{
    var hasErrors = false;

    if (string.IsNullOrWhiteSpace(config.HeroicConfigPath))
    {
        logger.WriteLine("[ERROR] HeroicConfigPath is not configured in appsettings.json", ConsoleColor.Red);
        hasErrors = true;
    }

    // At least one library path should be configured
    if (string.IsNullOrWhiteSpace(config.LegendaryLibraryPath) &&
        string.IsNullOrWhiteSpace(config.GogLibraryPath) &&
        string.IsNullOrWhiteSpace(config.NileLibraryPath))
    {
        logger.WriteLine(
            "[ERROR] No library paths configured. Set at least one of: LegendaryLibraryPath, GogLibraryPath, NileLibraryPath",
            ConsoleColor.Red);
        hasErrors = true;
    }

    return !hasErrors;
}

static void ResolvePaths(AppConfig config)
{
    config.HeroicConfigPath = ResolvePath(config.HeroicConfigPath);
    config.LegendaryLibraryPath = ResolvePath(config.LegendaryLibraryPath);
    config.GogLibraryPath = ResolvePath(config.GogLibraryPath);
    config.NileLibraryPath = ResolvePath(config.NileLibraryPath);
}

static string? ResolvePath(string? path)
{
    if (string.IsNullOrEmpty(path))
        return null;

    // Handle Windows %AppData%
    if (path.Contains("%AppData%", StringComparison.OrdinalIgnoreCase))
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return path.Replace("%AppData%", appData, StringComparison.OrdinalIgnoreCase);
    }

    // Handle Linux ~/
    if (path.StartsWith('~'))
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return path.Replace("~", home);
    }

    return path;
}