using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ApertureOS.Models;

namespace ApertureOS.Services;

public class AppSettings
{
    public string SteamGridDbApiKey { get; set; } = string.Empty;

    /// <summary>SteamID64 read from the embedded-browser login's session cookie, or empty if never logged in.</summary>
    public string SteamId64 { get; set; } = string.Empty;

    /// <summary>Fallback for full-library sync when not logged in via the embedded browser: a free key from steamcommunity.com/dev/apikey.</summary>
    public string SteamWebApiKey { get; set; } = string.Empty;

    /// <summary>"steamLoginSecure" cookie captured from an embedded-browser Steam login, or empty if never logged in that way.
    /// In memory only: SettingsService writes it to disk encrypted, as <see cref="SteamLoginSecureCookieProtected"/>.</summary>
    public string SteamLoginSecureCookie { get; set; } = string.Empty;

    /// <summary>The same session cookie, DPAPI-encrypted for the current Windows user - what settings.json actually holds.</summary>
    public string SteamLoginSecureCookieProtected { get; set; } = string.Empty;

    /// <summary>True = at startup, quietly fetch SteamGridDB art for games with no cover (or placeholder art).</summary>
    public bool AutoFetchMissingArt { get; set; }

    /// <summary>True = skip the normal window on launch and go straight into Console Mode.</summary>
    public bool LaunchInConsoleMode { get; set; }

    /// <summary>Name of the LibraryFilter tab last selected (desktop tabs or Console Mode's LB/RB), restored on the next launch.</summary>
    public string LastLibraryFilter { get; set; } = "All";

    /// <summary>Id (as a string) of the category chip last selected, empty for "All Categories" - restored the same way as LastLibraryFilter.</summary>
    public string LastCategoryFilterId { get; set; } = string.Empty;

    /// <summary>Configured emulators, each with its own exe, ROM folder, and console - used by the "Scan for Games" auto-add flow in Settings.</summary>
    public List<EmulatorConfig> Emulators { get; set; } = new();

    /// <summary>Folders the user has pointed the "Installed Games" scan at, to auto-detect PC games that aren't on Steam/Epic/GOG/Battle.net.</summary>
    public List<string> InstalledGameFolders { get; set; } = new();
}

public class SettingsService
{
    private readonly string _settingsFile;

    public SettingsService()
    {
        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ApertureOS");
        Directory.CreateDirectory(dataDir);
        _settingsFile = Path.Combine(dataDir, "settings.json");
    }

    public AppSettings Load()
    {
        if (!File.Exists(_settingsFile))
            return new AppSettings();

        var json = File.ReadAllText(_settingsFile);
        var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();

        if (!string.IsNullOrEmpty(settings.SteamLoginSecureCookieProtected))
        {
            settings.SteamLoginSecureCookie = Unprotect(settings.SteamLoginSecureCookieProtected);
        }
        else if (!string.IsNullOrEmpty(settings.SteamLoginSecureCookie))
        {
            // A settings.json from v1.1.1 or earlier, with the session cookie in clear text:
            // re-save it encrypted right away.
            Save(settings);
        }

        return settings;
    }

    public void Save(AppSettings settings)
    {
        // The Steam session cookie is a live login token, so it never goes to disk in clear text.
        var plainCookie = settings.SteamLoginSecureCookie;
        settings.SteamLoginSecureCookieProtected = Protect(plainCookie);
        settings.SteamLoginSecureCookie = string.Empty;

        try
        {
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsFile, json);
        }
        finally
        {
            settings.SteamLoginSecureCookie = plainCookie;
        }
    }

    private static readonly byte[] CookieEntropy = Encoding.UTF8.GetBytes("ApertureOS.SteamSession");

    private static string Protect(string plain) =>
        string.IsNullOrEmpty(plain)
            ? string.Empty
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), CookieEntropy, DataProtectionScope.CurrentUser));

    private static string Unprotect(string protectedValue)
    {
        try
        {
            return Encoding.UTF8.GetString(
                ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), CookieEntropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Encrypted by another Windows user/PC, or corrupted: treat as logged out.
            return string.Empty;
        }
    }
}
