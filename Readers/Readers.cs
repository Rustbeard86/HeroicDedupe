using System.Text.Json.Nodes;
using HeroicDedupe.Models;
using HeroicDedupe.Services;

namespace HeroicDedupe.Readers;

/// <summary>
///     Abstraction for reading game libraries from various stores.
/// </summary>
public interface IGameStoreReader
{
    Store StoreType { get; }
    Task<List<LocalGame>> ReadLibraryAsync(CancellationToken cancellationToken = default);
}

/// <summary>
///     Base class for store readers with shared JSON parsing logic.
/// </summary>
public abstract class BaseStoreReader(string? filePath, Store store, IConsoleLogger? logger = null) : IGameStoreReader
{
    private readonly IConsoleLogger _logger = logger ?? ConsoleLogger.Instance;

    public Store StoreType => store;

    public async Task<List<LocalGame>> ReadLibraryAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return [];

        try
        {
            await using var stream = File.OpenRead(filePath);
            var node = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken);
            return Parse(node);
        }
        catch (Exception ex)
        {
            _logger.WriteLine($"[ERROR] Error reading {StoreType} library: {ex.Message}", ConsoleColor.Red);
            return [];
        }
    }

    protected abstract List<LocalGame> Parse(JsonNode? root);

    /// <summary>
    ///     Helper to extract games from a JSON array using field mappings.
    /// </summary>
    protected List<LocalGame> ParseArray(JsonArray? array, string[] idFields, string[] titleFields,
        string[]? dateFields = null)
    {
        if (array is null) return [];

        List<LocalGame> games = [];

        foreach (var item in array)
        {
            var appId = GetFirstValue(item, idFields);
            var title = GetFirstValue(item, titleFields);
            var releaseDate = dateFields is not null ? GetReleaseDate(item, dateFields) : null;

            if (!string.IsNullOrWhiteSpace(appId) && !string.IsNullOrWhiteSpace(title))
                games.Add(new LocalGame(title, appId, StoreType, releaseDate));
        }

        return games;
    }

    /// <summary>
    ///     Safely gets a JsonArray from a node, handling both array and object structures.
    /// </summary>
    protected static JsonArray? GetLibraryArray(JsonNode? root, string propertyName = "library")
    {
        if (root is null) return null;

        // Try as direct array first
        if (root is JsonArray directArray)
            return directArray;

        // Try getting the property
        var libraryNode = root[propertyName];
        if (libraryNode is JsonArray libraryArray)
            return libraryArray;

        // Handle case where library contains an object with games inside
        if (libraryNode is JsonObject libraryObj)
            if (libraryObj["games"] is JsonArray gamesArray)
                return gamesArray;

        return null;
    }

    private static string? GetFirstValue(JsonNode? node, string[] fields)
    {
        foreach (var field in fields)
        {
            var value = node?[field]?.ToString();
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    /// <summary>
    ///     Extracts release date from the JSON node, searching in nested paths.
    /// </summary>
    private static DateTime? GetReleaseDate(JsonNode? node, string[] dateFields)
    {
        if (node is null) return null;

        foreach (var field in dateFields)
        {
            // Support dot-notation for nested paths (e.g., "extra.releaseDate")
            var parts = field.Split('.');
            var current = node;

            foreach (var part in parts)
            {
                current = current[part];
                if (current is null) break;
            }

            if (current is not null)
            {
                var dateStr = current.ToString();
                if (DateTime.TryParse(dateStr, out var date))
                    return date;
            }
        }

        return null;
    }
}

public sealed class LegendaryReader(string? path, IConsoleLogger? logger = null)
    : BaseStoreReader(path, Store.Epic, logger)
{
    protected override List<LocalGame> Parse(JsonNode? root)
    {
        var libraryArray = GetLibraryArray(root) ?? GetLibraryArray(root, "games");
        return ParseArray(libraryArray,
            ["app_name"],
            ["title", "app_title"],
            ["extra.releaseDate"]);
    }
}

public sealed class NileReader(string? path, IConsoleLogger? logger = null)
    : BaseStoreReader(path, Store.Amazon, logger)
{
    protected override List<LocalGame> Parse(JsonNode? root)
    {
        var libraryArray = GetLibraryArray(root) ?? GetLibraryArray(root, "games");
        return ParseArray(libraryArray,
            ["app_name", "id"],
            ["title"],
            ["extra.releaseDate"]);
    }
}

public sealed class GogReader(string? path, IConsoleLogger? logger = null)
    : BaseStoreReader(path, Store.Gog, logger)
{
    protected override List<LocalGame> Parse(JsonNode? root)
    {
        var libraryArray = GetLibraryArray(root)
                           ?? GetLibraryArray(root, "games")
                           ?? GetLibraryArray(root, "installed");

        return ParseArray(libraryArray,
            ["app_name", "id", "gameID"],
            ["title", "name"],
            ["extra.releaseDate", "extra.about.releaseDate"]);
    }
}