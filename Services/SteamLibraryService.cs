using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ApertureOS.Services;

/// <param name="IsFamilyShared">True when the license belongs to another Steam Family member.</param>
public sealed record SteamAppInfo(string AppId, string Name, bool IsFamilyShared = false);

/// <summary>
/// Local install/download state for one Steam app, read straight from its appmanifest_&lt;id&gt;.acf -
/// the same file Steam's own client updates live while downloading, so this reflects real progress
/// without any API. <see cref="IsInstalled"/> is only true once nothing is left pending
/// (StateFlags 4, confirmed against a real fully-installed manifest); while a download or update is
/// in progress it's false and <see cref="PercentComplete"/> reflects how far along it is.
/// </summary>
public sealed record SteamInstallProgress(bool IsInstalled, long BytesDownloaded, long BytesToDownload)
{
    public double? PercentComplete => BytesToDownload > 0 ? 100.0 * BytesDownloaded / BytesToDownload : null;
}

/// <summary>Thrown when a stored embedded-browser Steam session cookie has expired or been revoked.</summary>
public sealed class SteamSessionExpiredException : Exception
{
    public SteamSessionExpiredException() : base("Your Steam session has expired.")
    {
    }
}

/// <summary>Discovers Steam games either from local install records or the account's full owned library.</summary>
public sealed class SteamLibraryService
{
    /// <summary>
    /// Scans Steam's own local install records (steamapps/appmanifest_*.acf across every
    /// library folder) - no Steam Web API key or network access needed, but only finds games
    /// actually installed on this PC.
    /// </summary>
    public List<SteamAppInfo> GetInstalledGames(string? mySteamId64 = null)
    {
        var steamPath = FindSteamInstallPath();
        if (steamPath is null)
            return [];

        // Who "me" is, to tell family-shared installs apart: the logged-in SteamID if known, else
        // the account Steam last signed in with.
        if (string.IsNullOrWhiteSpace(mySteamId64))
            mySteamId64 = FindMostRecentSteamId(steamPath);

        var games = new List<SteamAppInfo>();
        foreach (var libraryFolder in GetLibraryFolders(steamPath))
        {
            var steamAppsDir = Path.Combine(libraryFolder, "steamapps");
            if (!Directory.Exists(steamAppsDir))
                continue;

            foreach (var manifestFile in Directory.GetFiles(steamAppsDir, "appmanifest_*.acf"))
            {
                if (ParseAppManifest(manifestFile, mySteamId64) is { } app)
                    games.Add(app);
            }
        }

        return games;
    }

    /// <summary>
    /// Every game the account owns, installed or not, authenticated with a manually-created Steam
    /// Web API key (steamcommunity.com/dev/apikey). Requires the profile's "game details" to be
    /// set to Public. Prefer <see cref="GetOwnedGamesViaSessionAsync"/> when a browser login is
    /// available - this is only the fallback for when it isn't.
    /// </summary>
    public async Task<List<SteamAppInfo>> GetOwnedGamesAsync(string steamId64, string webApiKey, CancellationToken ct = default)
    {
        var url = BuildOwnedGamesUrl(steamId64, "key", webApiKey);

        using var http = new HttpClient();
        using var response = await http.GetAsync(url, ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Steam Web API request failed ({(int)response.StatusCode} {response.StatusCode}). Double-check your Steam Web API key.");
        }

        return ParseOwnedGames(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>
    /// Same result as <see cref="GetOwnedGamesAsync"/> but authenticated with a real embedded-browser
    /// Steam login (see SteamWebLoginWindow) instead of a manually-created Web API key - this is
    /// what Playnite's default "web login" library import does. The community site's own React
    /// frontend calls this exact same official endpoint using the JWT embedded in the
    /// "steamLoginSecure" cookie as a bearer access_token instead of a developer key, so this just
    /// replicates that rather than scraping any community page directly (Valve retired the old
    /// unauthenticated XML/HTML scraping approaches entirely).
    /// </summary>
    public async Task<List<SteamAppInfo>> GetOwnedGamesViaSessionAsync(
        string steamId64, string steamLoginSecureCookie, CancellationToken ct = default)
    {
        var accessToken = ExtractAccessToken(steamLoginSecureCookie);
        var url = BuildOwnedGamesUrl(steamId64, "access_token", accessToken);

        using var http = new HttpClient();
        using var response = await http.GetAsync(url, ct);

        if (!response.IsSuccessStatusCode)
            throw new SteamSessionExpiredException();

        return ParseOwnedGames(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>
    /// Games other members of the user's Steam Family share with them, installed or not - the part
    /// GetOwnedGames never returns, since those licenses belong to someone else. Uses the same
    /// undocumented service the Steam client/store use for the family library
    /// (IFamilyGroupsService: GetFamilyGroupForUser -> GetSharedLibraryApps), authenticated with the
    /// same embedded-browser session token as <see cref="GetOwnedGamesViaSessionAsync"/>. Returns an
    /// empty list if the account isn't in a family. Undocumented means Valve can change it without
    /// notice, so callers should treat failures as "no family games", not as a failed sync.
    /// </summary>
    public async Task<List<SteamAppInfo>> GetFamilySharedGamesViaSessionAsync(
        string steamLoginSecureCookie, CancellationToken ct = default)
    {
        var accessToken = Uri.EscapeDataString(ExtractAccessToken(steamLoginSecureCookie));

        using var http = new HttpClient();
        http.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://store.steampowered.com");
        http.DefaultRequestHeaders.Referrer = new Uri("https://store.steampowered.com/");

        var groupJson = await GetFamilyApiJsonAsync(http,
            $"https://api.steampowered.com/IFamilyGroupsService/GetFamilyGroupForUser/v1/?access_token={accessToken}", ct);

        string? familyGroupId;
        using (var groupDoc = JsonDocument.Parse(groupJson))
        {
            if (!groupDoc.RootElement.TryGetProperty("response", out var response))
                return [];
            if (response.TryGetProperty("is_not_member_of_any_group", out var notMember) && notMember.ValueKind == JsonValueKind.True)
                return [];

            familyGroupId = response.TryGetProperty("family_groupid", out var idElement) ? JsonIdToString(idElement) : null;
        }

        if (string.IsNullOrEmpty(familyGroupId) || familyGroupId == "0")
            return [];

        // include_own=false: the caller already has the user's own games from GetOwnedGames.
        var libraryJson = await GetFamilyApiJsonAsync(http,
            "https://api.steampowered.com/IFamilyGroupsService/GetSharedLibraryApps/v1/" +
            $"?access_token={accessToken}&family_groupid={Uri.EscapeDataString(familyGroupId)}" +
            "&include_own=false&include_excluded=false&include_free=false&include_non_games=false&language=english", ct);

        using var libraryDoc = JsonDocument.Parse(libraryJson);
        if (!libraryDoc.RootElement.TryGetProperty("response", out var libraryResponse) ||
            !libraryResponse.TryGetProperty("apps", out var apps) ||
            apps.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var games = new List<SteamAppInfo>();
        foreach (var app in apps.EnumerateArray())
        {
            if (!app.TryGetProperty("appid", out var appIdElement) || JsonIdToString(appIdElement) is not { } appId)
                continue;

            // exclude_reason != 0 = listed but not playable from this account (e.g. not shareable).
            if (app.TryGetProperty("exclude_reason", out var excludeReason) &&
                excludeReason.ValueKind == JsonValueKind.Number && excludeReason.GetInt32() != 0)
            {
                continue;
            }

            var name = app.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
            games.Add(new SteamAppInfo(appId, string.IsNullOrWhiteSpace(name) ? "Unknown" : name, IsFamilyShared: true));
        }

        return games;
    }

    private static async Task<string> GetFamilyApiJsonAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new SteamSessionExpiredException();

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>Steam's JSON sends 64-bit ids as strings and small ones as numbers; accept both.</summary>
    private static string? JsonIdToString(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.GetRawText(),
        _ => null
    };

    private static string BuildOwnedGamesUrl(string steamId64, string authParamName, string authParamValue) =>
        "https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/" +
        $"?{authParamName}={Uri.EscapeDataString(authParamValue)}&steamid={Uri.EscapeDataString(steamId64)}" +
        "&include_appinfo=1&include_played_free_games=1&format=json";

    private static List<SteamAppInfo> ParseOwnedGames(string json)
    {
        using var doc = JsonDocument.Parse(json);

        // A profile whose "game details" aren't Public (or an otherwise unqueryable account) comes
        // back as an empty "response": {} rather than an explicit error, so this is worth
        // distinguishing for the user from a genuinely empty library.
        if (!doc.RootElement.TryGetProperty("response", out var responseElement) ||
            !responseElement.TryGetProperty("games", out var gamesElement))
        {
            throw new InvalidOperationException("No games found. Your Steam profile's \"game details\" privacy must be set to Public.");
        }

        return gamesElement.EnumerateArray()
            .Select(game => new SteamAppInfo(
                game.GetProperty("appid").GetInt32().ToString(),
                game.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "Unknown" : "Unknown"))
            .ToList();
    }

    /// <summary>
    /// The "steamLoginSecure" cookie's value is "{steamid}\|\|{JWT}" - still percent-encoded as the
    /// browser received it verbatim in Set-Cookie, since cookie values are echoed back byte-for-byte
    /// rather than auto-decoded. The JWT itself is what the community site passes around internally
    /// as a bearer access_token.
    /// </summary>
    private static string ExtractAccessToken(string steamLoginSecureCookie)
    {
        var decoded = Uri.UnescapeDataString(steamLoginSecureCookie);
        var separatorIndex = decoded.IndexOf("||", StringComparison.Ordinal);
        return separatorIndex >= 0 ? decoded[(separatorIndex + 2)..] : decoded;
    }

    /// <summary>Pulls the app ID back out of a "steam://rungameid/{appid}" ExePath, or null for a non-Steam game.</summary>
    public static string? TryGetSteamAppId(string exePath)
    {
        var match = Regex.Match(exePath, @"^steam://rungameid/(\d+)$", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Whether the Steam client currently reports this app as running. Steam keeps
    /// HKCU\Software\Valve\Steam\Apps\{appid}\Running and HKCU\Software\Valve\Steam\RunningAppID
    /// up to date for the game's whole process tree - including launcher hand-offs (RDR2 starts
    /// the Rockstar Games Launcher, which starts RDR2.exe) and anti-cheat-protected processes we
    /// can't open ourselves - so it's a better "is the game still up?" signal than any single
    /// process we could track.
    /// </summary>
    public static bool IsAppRunning(string appId)
    {
        try
        {
            using var appKey = Registry.CurrentUser.OpenSubKey($@"Software\Valve\Steam\Apps\{appId}");
            if (appKey?.GetValue("Running") is int running && running != 0)
                return true;

            using var steamKey = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return steamKey?.GetValue("RunningAppID") is int runningAppId && runningAppId.ToString() == appId;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Looks up the given app's local appmanifest across every Steam library folder. Null means no
    /// manifest exists anywhere - the game has never been installed and nothing is downloading.
    /// </summary>
    public SteamInstallProgress? GetInstallProgress(string appId)
    {
        var steamPath = FindSteamInstallPath();
        if (steamPath is null)
            return null;

        foreach (var libraryFolder in GetLibraryFolders(steamPath))
        {
            var manifestPath = Path.Combine(libraryFolder, "steamapps", $"appmanifest_{appId}.acf");
            if (!File.Exists(manifestPath))
                continue;

            string content;
            try
            {
                content = File.ReadAllText(manifestPath);
            }
            catch (IOException)
            {
                // Steam has the file open for writing at this exact instant; try again next poll
                // rather than reporting a false "not installed" for a mid-write manifest.
                return null;
            }

            var stateFlags = ParseAcfLongField(content, "StateFlags") ?? 0;
            var bytesToDownload = ParseAcfLongField(content, "BytesToDownload") ?? 0;
            var bytesDownloaded = ParseAcfLongField(content, "BytesDownloaded") ?? 0;

            return new SteamInstallProgress(stateFlags == 4, bytesDownloaded, bytesToDownload);
        }

        return null;
    }

    private static long? ParseAcfLongField(string content, string fieldName)
    {
        var match = Regex.Match(content, $"\"{fieldName}\"\\s*\"(\\d+)\"", RegexOptions.IgnoreCase);
        return match.Success ? long.Parse(match.Groups[1].Value) : null;
    }

    private static string? FindSteamInstallPath()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        return key?.GetValue("SteamPath") as string;
    }

    private static List<string> GetLibraryFolders(string steamPath)
    {
        // The registry's SteamPath uses forward slashes while libraryfolders.vdf uses escaped
        // backslashes, so the base Steam library (which is always also listed in the VDF) needs
        // normalizing before the duplicate check, or it gets scanned twice.
        var folders = new List<string>();
        void AddFolder(string path)
        {
            var normalized = Path.GetFullPath(path.Replace('/', '\\'));
            if (!folders.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                folders.Add(normalized);
        }

        AddFolder(steamPath);

        var vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdfPath))
        {
            var content = File.ReadAllText(vdfPath);
            foreach (Match match in Regex.Matches(content, "\"path\"\\s*\"([^\"]+)\""))
            {
                AddFolder(match.Groups[1].Value.Replace("\\\\", "\\"));
            }
        }

        return folders;
    }

    private static SteamAppInfo? ParseAppManifest(string manifestPath, string? mySteamId64)
    {
        var content = File.ReadAllText(manifestPath);
        var appIdMatch = Regex.Match(content, "\"appid\"\\s*\"(\\d+)\"", RegexOptions.IgnoreCase);
        var nameMatch = Regex.Match(content, "\"name\"\\s*\"([^\"]+)\"");
        if (!appIdMatch.Success || !nameMatch.Success)
            return null;

        // LastOwner = the account whose license the install uses: another family member's SteamID
        // for a family-shared game (checked against real installs with tools/detect-games.ps1).
        var ownerMatch = Regex.Match(content, "\"LastOwner\"\\s*\"(\\d+)\"", RegexOptions.IgnoreCase);
        var isFamilyShared = ownerMatch.Success && ownerMatch.Groups[1].Value != "0" &&
                             !string.IsNullOrWhiteSpace(mySteamId64) && ownerMatch.Groups[1].Value != mySteamId64;

        return new SteamAppInfo(appIdMatch.Groups[1].Value, nameMatch.Groups[1].Value, isFamilyShared);
    }

    /// <summary>The SteamID64 marked MostRecent in Steam's config/loginusers.vdf, or null.</summary>
    private static string? FindMostRecentSteamId(string steamPath)
    {
        try
        {
            var path = Path.Combine(steamPath.Replace('/', '\\'), "config", "loginusers.vdf");
            if (!File.Exists(path))
                return null;

            foreach (Match user in Regex.Matches(File.ReadAllText(path), "\"(\\d{17})\"\\s*\\{([^}]*)\\}"))
            {
                if (Regex.IsMatch(user.Groups[2].Value, "\"MostRecent\"\\s*\"1\""))
                    return user.Groups[1].Value;
            }
        }
        catch (IOException)
        {
        }

        return null;
    }
}
