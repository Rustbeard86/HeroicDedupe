using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HeroicDedupe.Models;

namespace HeroicDedupe.Services;

#region Cache Abstraction

/// <summary>
///     Cache entry storing release date with timestamp.
/// </summary>
public sealed record CacheEntry(DateTime? ReleaseDate, DateTime CachedAt);

/// <summary>
///     Abstraction for release date caching. Enables different storage backends.
/// </summary>
public interface IReleaseDateCache
{
    bool TryGet(string key, TimeSpan maxAge, out DateTime? releaseDate);
    void Set(string key, DateTime? releaseDate);
    void Save();
}

/// <summary>
///     File-based release date cache with JSON persistence.
/// </summary>
public sealed class FileReleaseDateCache : IReleaseDateCache
{
    private readonly Dictionary<string, CacheEntry> _cache;
    private readonly string _cachePath;
    private readonly IConsoleLogger _logger;
    private bool _modified;

    public FileReleaseDateCache(string cachePath, IConsoleLogger? logger = null)
    {
        _cachePath = cachePath;
        _logger = logger ?? ConsoleLogger.Instance;
        _cache = Load();
    }

    public bool TryGet(string key, TimeSpan maxAge, out DateTime? releaseDate)
    {
        if (_cache.TryGetValue(key, out var entry) && DateTime.UtcNow - entry.CachedAt < maxAge)
        {
            releaseDate = entry.ReleaseDate;
            return true;
        }

        releaseDate = null;
        return false;
    }

    public void Set(string key, DateTime? releaseDate)
    {
        _cache[key] = new CacheEntry(releaseDate, DateTime.UtcNow);
        _modified = true;
    }

    public void Save()
    {
        if (!_modified) return;

        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(_cache, options));
            _logger.WriteLine($"[Cache] Saved {_cache.Count} entries.", ConsoleColor.DarkGray);
            _modified = false;
        }
        catch (Exception ex)
        {
            _logger.WriteLine($"[Cache] Failed to save: {ex.Message}", ConsoleColor.DarkYellow);
        }
    }

    private Dictionary<string, CacheEntry> Load()
    {
        try
        {
            if (File.Exists(_cachePath))
            {
                var json = File.ReadAllText(_cachePath);
                var cache = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(json);
                if (cache is not null)
                {
                    _logger.WriteLine($"[Cache] Loaded {cache.Count} entries.", ConsoleColor.DarkGray);
                    return cache;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.WriteLine($"[Cache] Failed to load: {ex.Message}", ConsoleColor.DarkYellow);
        }

        return new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
    }
}

#endregion

#region IGDB Implementation

[method: JsonConstructor]
public sealed record IgdbGame(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("first_release_date")]
    long? FirstReleaseDate = null,
    [property: JsonPropertyName("category")]
    int? Category = null)
{
    public bool IsRemaster => Category is 8 or 9 or 10;

    public DateTime? ReleaseDate => FirstReleaseDate.HasValue
        ? DateTimeOffset.FromUnixTimeSeconds(FirstReleaseDate.Value).UtcDateTime
        : null;
}

[method: JsonConstructor]
public sealed record TwitchTokenResponse(
    [property: JsonPropertyName("access_token")]
    string AccessToken,
    [property: JsonPropertyName("expires_in")]
    int ExpiresIn,
    [property: JsonPropertyName("token_type")]
    string TokenType);

/// <summary>
///     IGDB metadata provider with rate-limited parallel requests.
/// </summary>
public sealed class IgdbService(
    IgdbConfig config,
    IReleaseDateCache cache,
    IConsoleLogger? logger = null) : IDisposable
{
    private const string TwitchAuthUrl = "https://id.twitch.tv/oauth2/token";
    private const string IgdbApiUrl = "https://api.igdb.com/v4";
    private const int MaxParallelRequests = 4;
    private static readonly TimeSpan CacheMaxAge = TimeSpan.FromDays(30);

    private readonly HttpClient _httpClient = new();
    private readonly IConsoleLogger _logger = logger ?? ConsoleLogger.Instance;
    private readonly SemaphoreSlim _rateLimiter = new(MaxParallelRequests);
    private string? _accessToken;
    private DateTime _tokenExpiry = DateTime.MinValue;

    public void Dispose()
    {
        _rateLimiter.Dispose();
        _httpClient.Dispose();
    }

    public async Task<List<LocalGame>> EnrichGamesAsync(
        List<LocalGame> games,
        CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);
        _logger.WriteLine("[IGDB] Enriching game metadata...", ConsoleColor.Cyan);

        var uniqueTitles = games
            .Select(g => g.Title)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var uncachedTitles = uniqueTitles
            .Where(t => !cache.TryGet(NormalizeKey(t), CacheMaxAge, out _))
            .ToList();

        var cachedCount = uniqueTitles.Count - uncachedTitles.Count;
        if (cachedCount > 0)
            _logger.WriteLine($"[IGDB] {cachedCount} in cache, {uncachedTitles.Count} need lookup.",
                ConsoleColor.DarkGray);

        if (uncachedTitles.Count > 0)
            await FetchBatchAsync(uncachedTitles, cancellationToken);

        cache.Save();

        // Build enriched list
        return games.Select(game =>
        {
            var key = NormalizeKey(game.Title);
            return cache.TryGet(key, CacheMaxAge, out var date) && date.HasValue && !game.ReleaseDate.HasValue
                ? game with { ReleaseDate = date }
                : game;
        }).ToList();
    }

    private async Task FetchBatchAsync(List<string> titles, CancellationToken cancellationToken)
    {
        var startTime = DateTime.UtcNow;
        var processed = 0;

        foreach (var batch in titles.Chunk(MaxParallelRequests))
        {
            var tasks = batch.Select(async title =>
            {
                await _rateLimiter.WaitAsync(cancellationToken);
                try
                {
                    await FetchAndCacheAsync(title, cancellationToken);
                }
                finally
                {
                    _rateLimiter.Release();
                }
            });

            await Task.WhenAll(tasks);
            processed += batch.Length;

            if (processed < titles.Count)
                await Task.Delay(1000, cancellationToken);

            if (processed % 50 < MaxParallelRequests || processed == titles.Count)
                _logger.WriteLine($"[IGDB] Processed {processed}/{titles.Count}...", ConsoleColor.DarkGray);
        }

        var elapsed = DateTime.UtcNow - startTime;
        _logger.WriteLine(
            $"[IGDB] Completed in {elapsed.TotalSeconds:F1}s ({titles.Count / elapsed.TotalSeconds:F1}/sec)",
            ConsoleColor.DarkGray);
    }

    private async Task FetchAndCacheAsync(string title, CancellationToken cancellationToken)
    {
        var key = NormalizeKey(title);
        try
        {
            var searchTitle = EscapeQuery(CleanTitle(title));
            var query = $"""
                         search "{searchTitle}";
                         fields id, name, first_release_date, category;
                         limit 5;
                         """;

            var games = await QueryAsync<IgdbGame[]>("games", query, cancellationToken);

            var releaseDate = games?.Length > 0
                ? games.OrderByDescending(g => Similarity(g.Name, title))
                    .ThenByDescending(g => g.IsRemaster)
                    .FirstOrDefault()?.ReleaseDate
                : null;

            cache.Set(key, releaseDate);
        }
        catch (Exception ex)
        {
            _logger.WriteLine($"[IGDB] Failed '{title}': {ex.Message}", ConsoleColor.DarkYellow);
        }
    }

    private async Task EnsureAuthenticatedAsync(CancellationToken cancellationToken)
    {
        if (_accessToken is not null && DateTime.UtcNow < _tokenExpiry) return;

        var content = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("client_id", config.ClientId!),
            new KeyValuePair<string, string>("client_secret", config.ClientSecret!),
            new KeyValuePair<string, string>("grant_type", "client_credentials")
        ]);

        var response = await _httpClient.PostAsync(TwitchAuthUrl, content, cancellationToken);
        response.EnsureSuccessStatusCode();

        var token = await response.Content.ReadFromJsonAsync<TwitchTokenResponse>(cancellationToken)
                    ?? throw new InvalidOperationException("Failed to get access token");

        _accessToken = token.AccessToken;
        _tokenExpiry = DateTime.UtcNow.AddSeconds(token.ExpiresIn - 60);
    }

    private async Task<T?> QueryAsync<T>(string endpoint, string query, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{IgdbApiUrl}/{endpoint}");
        request.Content = new StringContent(query, Encoding.UTF8, "text/plain");
        request.Headers.Add("Client-ID", config.ClientId);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken);
    }

    // Static helpers
    private static string CleanTitle(string title)
    {
        return title.Replace("\u2122", "").Replace("\u00AE", "").Replace("\u00A9", "")
            .Replace(" - ", " ").Replace(": ", " ")
            .Replace(" Remastered", "", StringComparison.OrdinalIgnoreCase)
            .Replace(" Definitive Edition", "", StringComparison.OrdinalIgnoreCase)
            .Replace(" Enhanced Edition", "", StringComparison.OrdinalIgnoreCase)
            .Replace(" GOTY", "", StringComparison.OrdinalIgnoreCase)
            .Replace(" HD", "", StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    private static string EscapeQuery(string s)
    {
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private static string NormalizeKey(string s)
    {
        return new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    }

    private static double Similarity(string a, string b)
    {
        var wa = a.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var wb = b.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        return wb.Count > 0 ? (double)wa.Intersect(wb).Count() / wa.Union(wb).Count() : 0;
    }
}

#endregion