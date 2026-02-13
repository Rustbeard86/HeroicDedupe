using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using HeroicDedupe.Models;

namespace HeroicDedupe.Services;

/// <summary>
///     Abstraction for console output operations.
/// </summary>
public interface IConsoleLogger
{
    void Write(string message);
    void WriteLine(string message);
    void WriteLine(string message, ConsoleColor color);
    string? ReadLine();
}

/// <summary>
///     Default console logger implementation.
/// </summary>
public sealed class ConsoleLogger : IConsoleLogger
{
    public static ConsoleLogger Instance { get; } = new();

    public void Write(string message)
    {
        Console.Write(message);
    }

    public void WriteLine(string message)
    {
        Console.WriteLine(message);
    }

    public void WriteLine(string message, ConsoleColor color)
    {
        Console.ForegroundColor = color;
        Console.WriteLine(message);
        Console.ResetColor();
    }

    public string? ReadLine()
    {
        return Console.ReadLine();
    }
}

public sealed class HeroicProcessManager(IConsoleLogger? logger = null)
{
    private const string ProcessName = "Heroic";
    private readonly IConsoleLogger _logger = logger ?? ConsoleLogger.Instance;

    /// <summary>
    ///     Tracks whether we closed Heroic so we can restart it on exit.
    /// </summary>
    public bool WasClosedByUs { get; private set; }

    /// <summary>
    ///     The executable path of the Heroic process we closed, used for restarting.
    /// </summary>
    private string? _heroicExePath;

    public bool IsHeroicRunning()
    {
        var processes = Process.GetProcessesByName(ProcessName);
        var isRunning = processes.Length > 0;

        foreach (var p in processes)
            p.Dispose();

        return isRunning;
    }

    public bool RequestClose()
    {
        var processes = Process.GetProcessesByName(ProcessName);
        if (processes.Length is 0)
            return true;

        try
        {
            // Capture the executable path before closing so we can restart later
            try
            {
                _heroicExePath = processes[0].MainModule?.FileName;
            }
            catch
            {
                // May fail due to access rights; we'll try common paths on restart
            }

            _logger.WriteLine($"\n[WARNING] Heroic Launcher is currently running ({processes.Length} instance(s)).",
                ConsoleColor.Yellow);
            _logger.WriteLine("   Changes to config.json will be overwritten if Heroic is not closed first.",
                ConsoleColor.Yellow);

            _logger.Write("   Would you like to close Heroic automatically now? [Y/n]: ");
            var input = _logger.ReadLine();

            if (!string.IsNullOrWhiteSpace(input) && !input.StartsWith("y", StringComparison.OrdinalIgnoreCase))
            {
                _logger.WriteLine(
                    "   [CANCELLED] Operation cancelled by user. Please close Heroic manually and retry.");
                return false;
            }

            _logger.WriteLine("   Attempting to close gracefully...");

            foreach (var p in processes)
                try
                {
                    p.CloseMainWindow();

                    // Smart wait: check every 500ms up to 3 seconds
                    for (var i = 0; i < 6 && !p.HasExited; i++)
                        Thread.Sleep(500);

                    if (!p.HasExited)
                    {
                        p.Kill();
                        _logger.WriteLine($"   [KILLED] Process {p.Id} was force closed.");
                    }
                    else
                    {
                        _logger.WriteLine($"   [OK] Process {p.Id} closed gracefully.");
                    }
                }
                catch (Exception ex)
                {
                    _logger.WriteLine($"   [ERROR] Error closing process {p.Id}: {ex.Message}", ConsoleColor.Red);
                    return false;
                }

            WasClosedByUs = true;
            return true;
        }
        finally
        {
            foreach (var p in processes)
                p.Dispose();
        }
    }

    /// <summary>
    ///     Restarts Heroic if it was previously closed by us.
    /// </summary>
    public void RestartHeroic()
    {
        if (!WasClosedByUs)
            return;

        // Try the captured path first, then fall back to common install locations
        var candidatePaths = new List<string>();

        if (!string.IsNullOrEmpty(_heroicExePath))
            candidatePaths.Add(_heroicExePath);

        // Common Windows install paths
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        candidatePaths.Add(Path.Combine(localAppData, "Programs", "heroic", "Heroic.exe"));

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        candidatePaths.Add(Path.Combine(programFiles, "Heroic", "Heroic.exe"));

        // Linux / Flatpak fallbacks
        candidatePaths.Add("/usr/bin/heroic");
        candidatePaths.Add("/usr/bin/flatpak");

        foreach (var path in candidatePaths)
        {
            if (!File.Exists(path))
                continue;

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                };

                // Handle Flatpak launch on Linux
                if (path.EndsWith("flatpak", StringComparison.OrdinalIgnoreCase))
                {
                    startInfo.Arguments = "run com.heroicgameslauncher.hgl";
                }

                Process.Start(startInfo);
                _logger.WriteLine("[OK] Heroic Launcher restarted.", ConsoleColor.Green);
                return;
            }
            catch (Exception ex)
            {
                _logger.WriteLine($"[WARNING] Failed to restart Heroic from '{path}': {ex.Message}",
                    ConsoleColor.Yellow);
            }
        }

        _logger.WriteLine("[WARNING] Could not restart Heroic - executable not found. Please start it manually.",
            ConsoleColor.Yellow);
    }
}

public sealed class DeduplicationService(AppConfig config, IConsoleLogger? logger = null)
{
    // Characters to strip completely (trademarks, special chars)
    private const string StripChars = "\u00AE\u2122\u00A9:-\u2013\u2014'\"\u2018\u2019\u201C\u201D.,:;!?";

    private static readonly string[] EditionSuffixes =
    [
        " remastered",
        " definitive edition",
        " enhanced edition",
        " complete edition",
        " game of the year edition",
        " goty edition",
        " goty",
        " hd",
        " director's cut",
        " ultimate edition"
    ];

    private static readonly string[] JunkSuffixes =
    [
        ": wild hunt",
        " standard edition"
    ];

    private readonly IConsoleLogger _logger = logger ?? ConsoleLogger.Instance;
    private readonly bool _preferEnhanced = config.PreferEnhancedEditions;
    private readonly List<Store> _priority = config.GetPriorityOrDefault();

    public List<LocalGame> IdentifyDuplicates(List<LocalGame> allGames)
    {
        List<LocalGame> gamesToHide = [];

        // Group by normalized base title (without edition suffixes)
        var groupedGames = allGames
            .GroupBy(g => NormalizeTitle(g.Title))
            .Where(g => !string.IsNullOrWhiteSpace(g.Key) && g.Count() > 1);

        foreach (var group in groupedGames)
        {
            // Sort by:
            // 1. If PreferEnhancedEditions: Enhanced edition FIRST (overrides store priority)
            // 2. Store priority (user preference)
            // 3. Enhanced edition (secondary if not primary)
            // 4. Newest release date as final tiebreaker
            var sortedGroup = _preferEnhanced
                ? group
                    .OrderByDescending(g => g.IsEnhancedEdition) // Enhanced first (overrides store)
                    .ThenBy(GetStorePriority) // Then store priority
                    .ThenByDescending(g => g.ReleaseDate ?? DateTime.MinValue)
                    .ToList()
                : group
                    .OrderBy(GetStorePriority) // Store priority first
                    .ThenByDescending(g => g.IsEnhancedEdition) // Then enhanced edition
                    .ThenByDescending(g => g.ReleaseDate ?? DateTime.MinValue)
                    .ToList();

            var winner = sortedGroup[0];
            var losers = sortedGroup[1..];

            // Show the normalized key for debugging/verification
            var normalizedKey = NormalizeTitle(winner.Title);

            _logger.WriteLine($"Match Group [key: {normalizedKey}]");
            _logger.WriteLine($"  [KEEP] {winner.Store,-6} | {winner.Title} ({winner.AppId}){FormatGameInfo(winner)}",
                ConsoleColor.Green);

            foreach (var loser in losers)
            {
                _logger.WriteLine($"  [HIDE] {loser.Store,-6} | {loser.Title} ({loser.AppId}){FormatGameInfo(loser)}",
                    ConsoleColor.Red);
                gamesToHide.Add(loser);
            }

            _logger.WriteLine(new string('-', 50));
        }

        return gamesToHide;
    }

    private int GetStorePriority(LocalGame game)
    {
        var index = _priority.IndexOf(game.Store);
        return index is -1 ? int.MaxValue : index;
    }

    private static string FormatGameInfo(LocalGame game)
    {
        var parts = new List<string>();

        if (game.IsEnhancedEdition)
            parts.Add(game.Edition.ToString());

        if (game.ReleaseDate.HasValue)
            parts.Add(game.ReleaseDate.Value.ToString("yyyy-MM-dd"));

        return parts.Count > 0 ? $" [{string.Join(", ", parts)}]" : "";
    }

    /// <summary>
    ///     Normalizes title by removing edition suffixes, special characters, and trademark symbols.
    ///     This groups "BioShock® 2" and "BioShock™ 2 Remastered" together.
    /// </summary>
    private static string NormalizeTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return string.Empty;

        var normalized = title.ToLowerInvariant();

        // Strip trademark and special characters first
        foreach (var c in StripChars)
            normalized = normalized.Replace(c.ToString(), "", StringComparison.Ordinal);

        // Remove edition suffixes (these differentiate versions)
        foreach (var suffix in EditionSuffixes)
            normalized = normalized.Replace(suffix, "", StringComparison.Ordinal);

        // Remove other junk
        foreach (var junk in JunkSuffixes)
            normalized = normalized.Replace(junk, "", StringComparison.Ordinal);

        // Strip to alphanumeric only (handles any remaining special chars)
        return string.Concat(normalized.Where(char.IsLetterOrDigit));
    }
}

public sealed class HeroicConfigModifier(AppConfig config, IConsoleLogger? logger = null)
{
    private readonly IConsoleLogger _logger = logger ?? ConsoleLogger.Instance;

    public async Task ApplyHiddenGamesAsync(List<LocalGame> gamesToHide, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(config.HeroicConfigPath) || !File.Exists(config.HeroicConfigPath))
            throw new FileNotFoundException("Heroic Config file not found.", config.HeroicConfigPath);

        _logger.WriteLine($"Reading config from: {config.HeroicConfigPath}");
        var jsonString = await File.ReadAllTextAsync(config.HeroicConfigPath, cancellationToken);

        var root = JsonNode.Parse(jsonString) ?? throw new InvalidOperationException("Failed to parse config.json");

        // Ensure path exists: games -> hidden
        var gamesNode = root["games"] ??= new JsonObject();
        var hiddenNode = gamesNode["hidden"]?.AsArray() ?? [];

        gamesNode["hidden"] ??= hiddenNode;

        // Load existing hidden IDs to prevent duplicates
        var existingIds = hiddenNode
            .Select(node => node?["appName"]?.GetValue<string>())
            .OfType<string>()
            .ToHashSet();

        var addedCount = 0;
        foreach (var game in gamesToHide.Where(g => !existingIds.Contains(g.AppId)))
        {
            hiddenNode.Add(new JsonObject
            {
                ["appName"] = game.AppId,
                ["title"] = game.Title
            });
            existingIds.Add(game.AppId);
            addedCount++;
        }

        if (addedCount > 0)
        {
            if (config.DryRun)
            {
                _logger.WriteLine(
                    $"\n[DRY RUN] Would have written {addedCount} new hidden games to config. No changes made.",
                    ConsoleColor.Cyan);
            }
            else
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                await File.WriteAllTextAsync(config.HeroicConfigPath, root.ToJsonString(options), cancellationToken);
                _logger.WriteLine($"\n[SUCCESS] Wrote {addedCount} hidden games to config.", ConsoleColor.Green);
            }
        }
        else
        {
            _logger.WriteLine("No new games needed to be hidden.");
        }
    }
}