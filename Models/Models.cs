using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using JetBrains.Annotations;

namespace HeroicDedupe.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Store
{
    Gog,
    Epic,
    Amazon
}

/// <summary>
///     Indicates if a game is a special edition (remastered, definitive, etc.)
/// </summary>
public enum EditionType
{
    Original,
    Remastered,
    Definitive,
    Enhanced,
    Complete,
    GameOfTheYear
}

/// <summary>
///     Immutable record representing a game from any store.
/// </summary>
public record LocalGame(string Title, string AppId, Store Store, DateTime? ReleaseDate = null)
{
    /// <summary>
    ///     Detects the edition type from the title.
    /// </summary>
    public EditionType Edition => DetectEdition(Title);

    /// <summary>
    ///     Returns true if this is any kind of enhanced/remastered edition.
    /// </summary>
    public bool IsEnhancedEdition => Edition != EditionType.Original;

    public override string ToString()
    {
        return $"[{Store}] {Title} ({AppId})";
    }

    private static EditionType DetectEdition(string title)
    {
        var lower = title.ToLowerInvariant();

        if (lower.Contains("remaster"))
            return EditionType.Remastered;
        if (lower.Contains("definitive"))
            return EditionType.Definitive;
        if (lower.Contains("enhanced"))
            return EditionType.Enhanced;
        if (lower.Contains("complete"))
            return EditionType.Complete;
        if (lower.Contains("game of the year") || lower.Contains("goty"))
            return EditionType.GameOfTheYear;

        return EditionType.Original;
    }
}

/// <summary>
///     Application configuration with sensible defaults. Properties set via JSON deserialization.
/// </summary>
[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]
public sealed class AppConfig
{
    // Paths need setters because they're resolved at runtime
    public string? HeroicConfigPath { get; set; }
    public string? LegendaryLibraryPath { get; set; }
    public string? GogLibraryPath { get; set; }
    public string? NileLibraryPath { get; set; }

    /// <summary>
    ///     Store priority order for deduplication. Set via JSON.
    /// </summary>
    public List<Store>? Priority { get; set; }

    public bool DryRun { get; set; } = true;

    public bool PreferEnhancedEditions { get; set; } = true;

    /// <summary>
    ///     Optional IGDB API configuration for enhanced metadata. Set via JSON.
    /// </summary>
    public IgdbConfig? Igdb { get; set; }

    /// <summary>
    ///     Gets the priority list, applying defaults if not configured.
    /// </summary>
    public List<Store> GetPriorityOrDefault()
    {
        return Priority is { Count: > 0 } ? Priority : [Store.Gog, Store.Epic, Store.Amazon];
    }
}

/// <summary>
///     Configuration for IGDB API integration (optional).
///     Get credentials from https://dev.twitch.tv/console/apps
///     Instantiated via JSON deserialization.
/// </summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]
public sealed class IgdbConfig
{
    /// <summary>
    ///     Twitch Developer Application Client ID. Set via JSON.
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>
    ///     Twitch Developer Application Client Secret. Set via JSON.
    /// </summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    ///     Whether to use IGDB for enrichment (even if credentials are set). Set via JSON.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     Returns true if IGDB is configured and enabled.
    /// </summary>
    public bool IsConfigured => Enabled &&
                                !string.IsNullOrWhiteSpace(ClientId) &&
                                !string.IsNullOrWhiteSpace(ClientSecret);
}