using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ApertureOS.Models;

namespace ApertureOS.Services;

/// <summary>Length, scores and genres for one game - the shared data behind Backlog and the Game Pass explorer.</summary>
public sealed class GameInfo
{
    public long? IgdbId { get; set; }

    /// <summary>Hours to the credits without extras (IGDB "hastily").</summary>
    public double? MainHours { get; set; }

    /// <summary>Hours with some extras (IGDB "normally").</summary>
    public double? ExtrasHours { get; set; }

    /// <summary>Hours to 100% (IGDB "completely").</summary>
    public double? CompletionistHours { get; set; }

    /// <summary>Critic aggregate 0-100 (IGDB aggregated_rating, built from critic reviews like Metacritic's).</summary>
    public double? CriticScore { get; set; }
    public int? CriticCount { get; set; }

    /// <summary>IGDB user rating 0-100.</summary>
    public double? UserScore { get; set; }

    public int? SteamPositivePercent { get; set; }
    public int? SteamReviewCount { get; set; }

    /// <summary>Steam's own label, e.g. "Very Positive".</summary>
    public string? SteamReviewSummary { get; set; }

    public List<string> Genres { get; set; } = new();
    public int? ReleaseYear { get; set; }
    public DateTime FetchedAt { get; set; }

    /// <summary>One line for dialogs/tiles, e.g. "~12 h main story · Critics 85 · Steam 92% (Very Positive)".</summary>
    public string Summary()
    {
        var parts = new List<string>();
        if (MainHours is { } main)
            parts.Add($"~{FormatHours(main)} main story");
        if (CriticScore is { } critic)
            parts.Add($"Critics {critic:0}");
        if (SteamPositivePercent is { } positive)
            parts.Add(SteamReviewSummary is { Length: > 0 } label ? $"Steam {positive}% ({label})" : $"Steam {positive}%");
        return string.Join(" · ", parts);
    }

    private static string FormatHours(double hours) => hours < 10 ? $"{hours:0.#} h" : $"{hours:0} h";
}

/// <summary>
/// Fetches and caches <see cref="GameInfo"/> from IGDB (game length via game_time_to_beats, critic
/// and user scores, genres) and Steam (review summary), stored in %APPDATA%\ApertureOS\gameinfo.json.
///
/// IGDB needs a free Twitch developer app (Client ID + Secret, client-credentials flow). Steam
/// games are matched exactly through IGDB's external_games (source 1 = Steam, uid = app id), in
/// batches of hundreds per request; everything else (Game Pass, Epic, manual) by name search.
/// </summary>
public sealed class GameInfoService
{
    public static GameInfoService Shared { get; } = new();

    private const int SteamExternalSource = 1;
    private const int BatchSize = 400;
    private static readonly TimeSpan RefreshAfter = TimeSpan.FromDays(30);

    // IGDB allows 4 requests/second; stay comfortably under it.
    private static readonly TimeSpan IgdbSpacing = TimeSpan.FromMilliseconds(300);

    private readonly string _cacheFile;
    private Dictionary<string, GameInfo>? _cache;

    private GameInfoService()
    {
        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ApertureOS");
        Directory.CreateDirectory(dataDir);
        _cacheFile = Path.Combine(dataDir, "gameinfo.json");
    }

    public static string KeyFor(Game game) =>
        SteamLibraryService.TryGetSteamAppId(game.ExePath) is { } appId
            ? $"steam:{appId}"
            : $"exe:{game.ExePath.ToLowerInvariant()}";

    public GameInfo? Get(Game game) => Cache.GetValueOrDefault(KeyFor(game));

    private Dictionary<string, GameInfo> Cache
    {
        get
        {
            if (_cache is not null)
                return _cache;

            try
            {
                _cache = File.Exists(_cacheFile)
                    ? JsonSerializer.Deserialize<Dictionary<string, GameInfo>>(File.ReadAllText(_cacheFile)) ?? new()
                    : new();
            }
            catch (Exception ex)
            {
                DebugLog.Write($"GameInfoService: cache unreadable, starting fresh: {ex.Message}");
                _cache = new();
            }

            return _cache;
        }
    }

    private void SaveCache() =>
        File.WriteAllText(_cacheFile, JsonSerializer.Serialize(Cache, new JsonSerializerOptions { WriteIndented = true }));

    /// <summary>
    /// Fetches info for every non-hidden game without (fresh) cached info. Returns how many games
    /// got an entry. Throws on bad IGDB credentials so the caller can say so.
    /// </summary>
    public async Task<int> FetchMissingAsync(string clientId, string clientSecret, IReadOnlyList<Game> games,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var targets = games
            .Where(g => !g.IsHidden)
            .Where(g => Get(g) is not { } info || DateTime.UtcNow - info.FetchedAt > RefreshAfter)
            .DistinctBy(KeyFor)
            .ToList();
        if (targets.Count == 0)
            return 0;

        using var http = new HttpClient();
        using var steamHttp = new HttpClient(); // separate: Steam must not receive the IGDB auth headers
        progress?.Report("Connecting to IGDB...");
        var token = await GetTwitchTokenAsync(http, clientId, clientSecret, ct);
        http.DefaultRequestHeaders.Add("Client-ID", clientId);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // 1. Match games to IGDB ids: Steam by app id in batches, the rest by name.
        var igdbIdByKey = new Dictionary<string, long>();
        var steamTargets = targets
            .Select(g => (Game: g, AppId: SteamLibraryService.TryGetSteamAppId(g.ExePath)))
            .Where(t => t.AppId is not null)
            .ToList();

        foreach (var chunk in steamTargets.Chunk(BatchSize))
        {
            progress?.Report($"Matching Steam games... ({igdbIdByKey.Count}/{steamTargets.Count})");
            var uids = string.Join(",", chunk.Select(t => $"\"{t.AppId}\""));
            var rows = await QueryAsync(http,
                "external_games",
                $"fields game,uid; where external_game_source = {SteamExternalSource} & uid = ({uids}); limit 500;", ct);
            foreach (var row in rows)
            {
                if (row.TryGetProperty("uid", out var uid) && row.TryGetProperty("game", out var gameId) &&
                    gameId.ValueKind == JsonValueKind.Number)
                {
                    igdbIdByKey.TryAdd($"steam:{uid.GetString()}", gameId.GetInt64());
                }
            }
        }

        var byName = targets.Where(g => !igdbIdByKey.ContainsKey(KeyFor(g))).ToList();
        for (var i = 0; i < byName.Count; i++)
        {
            progress?.Report($"Matching other games by name... {i + 1}/{byName.Count}");
            var name = byName[i].Name.Replace("\\", "").Replace("\"", "\\\"");
            var rows = await QueryAsync(http, "games", $"search \"{name}\"; fields id; limit 1;", ct);
            if (rows.FirstOrDefault() is { ValueKind: JsonValueKind.Object } hit && hit.TryGetProperty("id", out var id))
                igdbIdByKey[KeyFor(byName[i])] = id.GetInt64();
        }

        // 2. Details and time-to-beat for all matched ids, in batches.
        var ids = igdbIdByKey.Values.Distinct().ToList();
        var details = new Dictionary<long, JsonElement>();
        var timeToBeat = new Dictionary<long, JsonElement>();
        foreach (var chunk in ids.Chunk(BatchSize))
        {
            progress?.Report($"Fetching scores and length... ({details.Count}/{ids.Count})");
            var idList = string.Join(",", chunk);
            foreach (var row in await QueryAsync(http, "games",
                         $"fields name,aggregated_rating,aggregated_rating_count,rating,first_release_date,genres.name; where id = ({idList}); limit 500;", ct))
            {
                details[row.GetProperty("id").GetInt64()] = row;
            }

            foreach (var row in await QueryAsync(http, "game_time_to_beats",
                         $"fields game_id,hastily,normally,completely,count; where game_id = ({idList}); limit 500;", ct))
            {
                if (row.TryGetProperty("game_id", out var gameId))
                    timeToBeat[gameId.GetInt64()] = row;
            }
        }

        // 3. Build entries; Steam games also get their Steam review summary.
        var updated = 0;
        for (var i = 0; i < targets.Count; i++)
        {
            var game = targets[i];
            var key = KeyFor(game);
            var info = new GameInfo { FetchedAt = DateTime.UtcNow };

            if (igdbIdByKey.TryGetValue(key, out var igdbId))
            {
                info.IgdbId = igdbId;
                if (details.TryGetValue(igdbId, out var d))
                {
                    info.CriticScore = GetDouble(d, "aggregated_rating");
                    info.CriticCount = (int?)GetDouble(d, "aggregated_rating_count");
                    info.UserScore = GetDouble(d, "rating");
                    if (GetDouble(d, "first_release_date") is { } unix)
                        info.ReleaseYear = DateTimeOffset.FromUnixTimeSeconds((long)unix).Year;
                    if (d.TryGetProperty("genres", out var genres) && genres.ValueKind == JsonValueKind.Array)
                    {
                        info.Genres = genres.EnumerateArray()
                            .Select(g => g.TryGetProperty("name", out var n) ? n.GetString() : null)
                            .OfType<string>().ToList();
                    }
                }

                if (timeToBeat.TryGetValue(igdbId, out var t))
                {
                    info.MainHours = SecondsToHours(GetDouble(t, "hastily"));
                    info.ExtrasHours = SecondsToHours(GetDouble(t, "normally"));
                    info.CompletionistHours = SecondsToHours(GetDouble(t, "completely"));
                }
            }

            if (SteamLibraryService.TryGetSteamAppId(game.ExePath) is { } appId)
            {
                progress?.Report($"Fetching Steam reviews... {i + 1}/{targets.Count}");
                await TryAddSteamReviewsAsync(steamHttp, appId, info, ct);
            }

            Cache[key] = info;
            updated++;

            // Save as we go: a long first run that gets interrupted keeps what it already fetched.
            if (updated % 50 == 0)
                SaveCache();
        }

        SaveCache();
        return updated;
    }

    private static async Task<string> GetTwitchTokenAsync(HttpClient http, string clientId, string clientSecret, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["grant_type"] = "client_credentials"
        });
        using var response = await http.PostAsync("https://id.twitch.tv/oauth2/token", content, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("IGDB login failed - check the Twitch Client ID and Secret in Settings.");

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("access_token").GetString()
               ?? throw new InvalidOperationException("IGDB login returned no token.");
    }

    private static async Task<JsonElement[]> QueryAsync(HttpClient http, string endpoint, string body, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            await Task.Delay(IgdbSpacing, ct);
            using var content = new StringContent(body, Encoding.UTF8, "text/plain");
            using var response = await http.PostAsync($"https://api.igdb.com/v4/{endpoint}", content, ct);
            if ((int)response.StatusCode == 429 && attempt < 3)
            {
                await Task.Delay(TimeSpan.FromSeconds(1 + attempt), ct);
                continue;
            }

            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToArray()
                : [];
        }
    }

    private static async Task TryAddSteamReviewsAsync(HttpClient http, string appId, GameInfo info, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(
                $"https://store.steampowered.com/appreviews/{appId}?json=1&language=all&purchase_type=all&num_per_page=0", ct);
            if (!response.IsSuccessStatusCode)
                return;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("query_summary", out var summary))
                return;

            var total = GetDouble(summary, "total_reviews");
            var positive = GetDouble(summary, "total_positive");
            if (total is > 0 && positive is { } p)
            {
                info.SteamReviewCount = (int)total.Value;
                info.SteamPositivePercent = (int)Math.Round(100 * p / total.Value);
            }

            if (summary.TryGetProperty("review_score_desc", out var desc) && desc.ValueKind == JsonValueKind.String)
                info.SteamReviewSummary = desc.GetString();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            // Reviews are a nice-to-have; a hiccup just leaves them empty until the next refresh.
        }
    }

    private static double? GetDouble(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    private static double? SecondsToHours(double? seconds) => seconds is > 0 ? Math.Round(seconds.Value / 3600, 1) : null;
}
