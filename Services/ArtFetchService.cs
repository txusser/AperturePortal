using System.IO;
using ApertureOS.Models;

namespace ApertureOS.Services;

/// <summary>
/// Finds and fills in missing cover/header art from SteamGridDB for games already in the library -
/// used by Settings' "Fetch missing art" button and the optional at-startup pass.
///
/// "Missing" also covers placeholder art: a Game Pass package's own square logo (stored when the
/// Xbox sync had nothing better), which is replaced as soon as SteamGridDB has a real grid.
/// </summary>
public static class ArtFetchService
{
    // A few requests in flight at once: much faster than one-by-one over a large library without
    // hammering SteamGridDB into rate-limiting us.
    private const int MaxConcurrent = 3;

    public static bool NeedsArt(Game game) =>
        string.IsNullOrWhiteSpace(game.CoverImagePath) ||
        !File.Exists(game.CoverImagePath) ||
        game.CoverIsPlaceholder ||
        // Xbox games synced before CoverIsPlaceholder existed: logo only, never had SteamGridDB art.
        (game.Platform == GamePlatform.Xbox && string.IsNullOrWhiteSpace(game.HeaderImagePath));

    /// <summary>
    /// Fetches art for every game in <paramref name="games"/> that <see cref="NeedsArt"/>. Runs its
    /// continuations on the caller's context (call it from the UI thread) and only touches the
    /// games' properties - saving the library is the caller's job. Returns how many games got art.
    /// </summary>
    public static async Task<int> FetchMissingArtAsync(
        string steamGridDbApiKey, GameLibraryService libraryService, IReadOnlyList<Game> games,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var targets = games.Where(NeedsArt).ToList();
        if (targets.Count == 0 || string.IsNullOrWhiteSpace(steamGridDbApiKey))
            return 0;

        using var steamGrid = new SteamGridDbService(steamGridDbApiKey);
        using var gate = new SemaphoreSlim(MaxConcurrent);
        var done = 0;
        var updated = 0;

        async Task ProcessAsync(Game game)
        {
            await gate.WaitAsync(ct);
            try
            {
                if (await TryFetchForGameAsync(steamGrid, libraryService, game, ct))
                    updated++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Best-effort per game: no SteamGridDB entry, a rate-limit hiccup, a bad download.
                DebugLog.Write($"Missing-art fetch failed for '{game.Name}': {ex.Message}");
            }
            finally
            {
                gate.Release();
                done++;
                progress?.Report($"Fetching missing art... {done}/{targets.Count}");
            }
        }

        await Task.WhenAll(targets.Select(ProcessAsync));
        return updated;
    }

    private static async Task<bool> TryFetchForGameAsync(
        SteamGridDbService steamGrid, GameLibraryService libraryService, Game game, CancellationToken ct)
    {
        // Steam games: exact lookup by app id. Everything else (Game Pass, Epic, GOG, manual):
        // best match by name, same as the "Find Art" button.
        int? gameId = SteamLibraryService.TryGetSteamAppId(game.ExePath) is { } appId
            ? await steamGrid.GetGameIdBySteamAppIdAsync(appId, ct)
            : (await steamGrid.SearchGamesAsync(game.Name, ct)).FirstOrDefault()?.Id;

        if (gameId is not { } id)
            return false;

        var gotCover = false;
        var grids = await steamGrid.GetGridsAsync(id, ct);
        if (grids.Count > 0)
        {
            game.CoverImagePath = await DownloadAndStoreAsync(steamGrid, grids[0].Url,
                path => libraryService.StoreCoverImage(path, game.Name), ct);
            game.CoverIsPlaceholder = false;
            gotCover = true;
        }

        if (string.IsNullOrWhiteSpace(game.HeaderImagePath) || !File.Exists(game.HeaderImagePath))
        {
            var heroes = await steamGrid.GetHeroesAsync(id, ct);
            if (heroes.Count > 0)
            {
                game.HeaderImagePath = await DownloadAndStoreAsync(steamGrid, heroes[0].Url,
                    path => libraryService.StoreHeaderImage(path, game.Name), ct);
            }
        }

        return gotCover;
    }

    private static async Task<string> DownloadAndStoreAsync(
        SteamGridDbService steamGrid, string url, Func<string, string> store, CancellationToken ct)
    {
        var bytes = await steamGrid.DownloadImageAsync(url, ct);
        var extension = Path.GetExtension(new Uri(url).AbsolutePath);
        if (string.IsNullOrEmpty(extension))
            extension = ".png";

        var tempPath = Path.Combine(Path.GetTempPath(), $"sgdb_{Guid.NewGuid():N}{extension}");
        await File.WriteAllBytesAsync(tempPath, bytes, ct);
        try
        {
            return store(tempPath);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }
}
