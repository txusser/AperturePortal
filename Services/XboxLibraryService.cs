using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Windows.Management.Deployment;

namespace ApertureOS.Services;

public sealed record XboxGameInfo(string Aumid, string Name, string ExePath, string? LogoPath);

/// <summary>
/// Finds Xbox / PC Game Pass games installed on this PC - no login needed. These are Store (MSIX)
/// packages, and the ones that are PC games ship a MicrosoftGame.config (the GDK game manifest) at
/// their root, which is the marker used here. They can't be launched by exe path - the files sit in
/// protected folders and need package identity to run - so synced entries store
/// "shell:AppsFolder\{AUMID}" and launch through Windows' own app activation instead.
/// </summary>
public sealed class XboxLibraryService
{
    public const string ExePathPrefix = @"shell:AppsFolder\";

    public List<XboxGameInfo> GetInstalledGames()
    {
        var games = new List<XboxGameInfo>();

        try
        {
            // Empty user SID = the current user; works without admin rights.
            foreach (var package in new PackageManager().FindPackagesForUser(string.Empty))
            {
                try
                {
                    if (TryReadGame(package) is { } game)
                        games.Add(game);
                }
                catch (Exception ex)
                {
                    // One odd package (half-installed, being updated) shouldn't sink the whole scan.
                    DebugLog.Write($"XboxLibraryService: skipped a package: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"XboxLibraryService: package enumeration failed: {ex}");
        }

        return games;
    }

    private static XboxGameInfo? TryReadGame(Windows.ApplicationModel.Package package)
    {
        if (package.IsFramework || package.IsResourcePackage)
            return null;

        var root = package.InstalledPath;
        if (string.IsNullOrEmpty(root))
            return null;

        var configPath = Path.Combine(root, "MicrosoftGame.config");
        if (!File.Exists(configPath))
            return null;

        var entry = package.GetAppListEntries().FirstOrDefault();
        if (entry is null || string.IsNullOrEmpty(entry.AppUserModelId))
            return null;

        var doc = XDocument.Load(configPath);
        var shellVisuals = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "ShellVisuals");
        string? Attr(string name) =>
            shellVisuals?.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value;

        var name = FirstUsable(entry.DisplayInfo?.DisplayName, package.DisplayName, Attr("DefaultDisplayName"))
                   ?? package.Id.Name;

        var logo = ResolveAsset(root, Attr("Square480x480Logo"))
                   ?? ResolveAsset(root, Attr("Square150x150Logo"))
                   ?? ResolveAsset(root, Attr("StoreLogo"));

        return new XboxGameInfo(entry.AppUserModelId, name, ExePathPrefix + entry.AppUserModelId, logo);
    }

    /// <summary>The AUMID out of a "shell:AppsFolder\{AUMID}" ExePath, or null for any other game.</summary>
    public static string? TryGetAumid(string exePath) =>
        exePath.StartsWith(ExePathPrefix, StringComparison.OrdinalIgnoreCase) ? exePath[ExePathPrefix.Length..] : null;

    /// <summary>
    /// Starts the game through IApplicationActivationManager, which - unlike shell-executing the
    /// shell:AppsFolder path - hands back the started process's ID, so the session can track it.
    /// Returns null if that process already exited (a launch helper that handed off to the real
    /// game), which MainWindow treats like any other bootstrapper hand-off.
    /// </summary>
    public static Process? Launch(string aumid)
    {
        var manager = (IApplicationActivationManager)new ApplicationActivationManager();
        var hr = manager.ActivateApplication(aumid, null, ActivateOptions.None, out var processId);
        Marshal.ThrowExceptionForHR(hr);

        try
        {
            return Process.GetProcessById((int)processId);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string? FirstUsable(params string?[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c) &&
                                       !c.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Resolves a manifest asset path. MSIX assets often exist only with a qualifier in the name
    /// ("Logo.png" declared, "Logo.scale-200.png" on disk), so fall back to the largest qualified
    /// variant next to it.
    /// </summary>
    private static string? ResolveAsset(string root, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return null;

        var path = Path.Combine(root, relativePath.Replace('/', '\\'));
        if (File.Exists(path))
            return path;

        var dir = Path.GetDirectoryName(path);
        if (dir is null || !Directory.Exists(dir))
            return null;

        var pattern = Path.GetFileNameWithoutExtension(path) + ".*" + Path.GetExtension(path);
        return Directory.EnumerateFiles(dir, pattern)
            .OrderByDescending(f => new FileInfo(f).Length)
            .FirstOrDefault();
    }
}

internal enum ActivateOptions
{
    None = 0
}

[ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IApplicationActivationManager
{
    [PreserveSig]
    int ActivateApplication(
        [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
        [MarshalAs(UnmanagedType.LPWStr)] string? arguments,
        ActivateOptions options,
        out uint processId);
}

[ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
internal class ApplicationActivationManager
{
}
