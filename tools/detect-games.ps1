# detect-games.ps1 - READ-ONLY probe: lists installed Steam (owned + family) and
# Xbox / Game Pass games. Changes nothing, needs no login and no network.
#
# Run in Windows PowerShell (not PowerShell 7 - the Appx cmdlets need 5.1):
#   powershell -ExecutionPolicy Bypass -File .\detect-games.ps1
#
# Output: a table in the console + games.json next to the script.
# Steam account IDs are replaced by labels ("me", "family-1", ...).

$ErrorActionPreference = 'Stop'
$here  = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
$games = New-Object System.Collections.Generic.List[object]

function Unescape([string]$s) { $s -replace '\\(.)', '$1' }

# First occurrence of each "key" "value" pair (top-level keys come first in .acf files)
function Get-VdfFields([string]$text) {
    $h = @{}
    foreach ($m in [regex]::Matches($text, '(?m)^\s*"([^"]+)"\s+"((?:[^"\\]|\\.)*)"')) {
        $k = $m.Groups[1].Value
        if (-not $h.ContainsKey($k)) { $h[$k] = Unescape $m.Groups[2].Value }
    }
    $h
}

# ------------------------------------------------------------------ Steam
$steamPath = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
if (-not $steamPath) {
    $steamPath = (Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam' -ErrorAction SilentlyContinue).InstallPath
}

$me = $null
$ownerLabels = @{}
function Get-OwnerLabel($id) {
    if (-not $id -or $id -eq '0') { return 'unknown' }
    if ($id -eq $script:me) { return 'me' }
    if (-not $script:ownerLabels.ContainsKey($id)) {
        $script:ownerLabels[$id] = "family-$($script:ownerLabels.Count + 1)"
    }
    $script:ownerLabels[$id]
}

if ($steamPath) {
    $steamPath = [IO.Path]::GetFullPath($steamPath.Replace('/', '\'))

    # Who am I? Active user (Steam running) first, else the most recent login.
    $au = (Get-ItemProperty 'HKCU:\Software\Valve\Steam\ActiveProcess' -ErrorAction SilentlyContinue).ActiveUser
    if ($au) {
        $au = [int64]$au; if ($au -lt 0) { $au += 4294967296 }
        $me = [string](76561197960265728 + $au)
    } else {
        $lu = Join-Path $steamPath 'config\loginusers.vdf'
        if (Test-Path -LiteralPath $lu) {
            $t = Get-Content -Raw -LiteralPath $lu
            foreach ($m in [regex]::Matches($t, '"(\d{17})"\s*\{([^}]*)\}')) {
                if ($m.Groups[2].Value -match '"MostRecent"\s+"1"') { $me = $m.Groups[1].Value }
            }
        }
    }

    # Library folders
    $libs = @($steamPath)
    $lf = Join-Path $steamPath 'steamapps\libraryfolders.vdf'
    if (Test-Path -LiteralPath $lf) {
        $t = Get-Content -Raw -LiteralPath $lf
        foreach ($m in [regex]::Matches($t, '"path"\s+"((?:[^"\\]|\\.)*)"')) {
            $libs += (Unescape $m.Groups[1].Value)
        }
    }
    $libs = @($libs | ForEach-Object { $_.TrimEnd('\') } | Sort-Object -Unique)

    $cache   = Join-Path $steamPath 'appcache\librarycache'
    $toolIds = @('228980')   # Steamworks Common Redistributables

    foreach ($lib in $libs) {
        $sa = Join-Path $lib 'steamapps'
        if (-not (Test-Path -LiteralPath $sa)) { continue }
        foreach ($f in Get-ChildItem -LiteralPath $sa -Filter 'appmanifest_*.acf' -File) {
            try {
                $a = Get-VdfFields (Get-Content -Raw -LiteralPath $f.FullName)
                $appid = if ($a['appid']) { $a['appid'] } else { $f.BaseName -replace '\D', '' }
                if ($toolIds -contains $appid) { continue }

                $flags = 0; [void][int]::TryParse([string]$a['StateFlags'], [ref]$flags)

                # Cover art from Steam's local cache (old flat layout or new per-app folder)
                $cover = $null
                $flat = Join-Path $cache "${appid}_library_600x900.jpg"
                if (Test-Path -LiteralPath $flat) { $cover = $flat }
                else {
                    $dir = Join-Path $cache $appid
                    if (Test-Path -LiteralPath $dir) {
                        $hit = Get-ChildItem -LiteralPath $dir -Recurse -File -Filter 'library_600x900*' -ErrorAction SilentlyContinue |
                               Select-Object -First 1
                        if ($hit) { $cover = $hit.FullName }
                    }
                }

                $games.Add([pscustomobject]@{
                    source      = 'steam'
                    id          = $appid
                    title       = $a['name']
                    installed   = (($flags -band 4) -ne 0)   # StateFlags bit 4 = fully installed
                    owner       = Get-OwnerLabel $a['LastOwner']
                    installPath = Join-Path $sa ("common\" + $a['installdir'])
                    launch      = "steam://rungameid/$appid"
                    cover       = $cover
                })
            } catch { Write-Warning "Steam manifest $($f.Name): $_" }
        }
    }
} else {
    Write-Warning 'Steam not found in the registry.'
}

# ------------------------------------------------------------- Xbox / Game Pass
# A Store package with a MicrosoftGame.config at its root is a PC (GDK) game.
$startApps = @{}
try { Get-StartApps | ForEach-Object { $startApps[$_.AppID] = $_.Name } } catch {}

foreach ($pkg in Get-AppxPackage) {
    try {
        if ($pkg.IsFramework -or -not $pkg.InstallLocation) { continue }
        $cfgPath = Join-Path $pkg.InstallLocation 'MicrosoftGame.config'
        if (-not (Test-Path -LiteralPath $cfgPath)) { continue }

        [xml]$cfg = Get-Content -Raw -LiteralPath $cfgPath
        $man    = Get-AppxPackageManifest -Package $pkg.PackageFullName
        $appIds = @($man.Package.Applications.Application | ForEach-Object { $_.Id } | Where-Object { $_ })
        if (-not $appIds) { continue }

        # AUMID = PackageFamilyName!AppId ; prefer the one the Start menu knows (gives a clean name)
        $aumid = $null; $name = $null
        foreach ($id in $appIds) {
            $cand = "$($pkg.PackageFamilyName)!$id"
            if ($startApps.ContainsKey($cand)) { $aumid = $cand; $name = $startApps[$cand]; break }
        }
        if (-not $aumid) { $aumid = "$($pkg.PackageFamilyName)!$($appIds[0])" }

        $sv = $cfg.Game.ShellVisuals
        if (-not $name -and $sv.DefaultDisplayName -and $sv.DefaultDisplayName -notlike 'ms-resource*') {
            $name = $sv.DefaultDisplayName
        }
        if (-not $name) { $name = $pkg.Name }

        $logoRel = @($sv.Square480x480Logo, $sv.Square150x150Logo, $sv.StoreLogo) | Where-Object { $_ } | Select-Object -First 1
        $logo = if ($logoRel) { Join-Path $pkg.InstallLocation $logoRel } else { $null }
        if ($logo -and -not (Test-Path -LiteralPath $logo)) { $logo = "$logo (declared, file not found)" }

        $games.Add([pscustomobject]@{
            source      = 'xbox'
            id          = $aumid
            title       = $name
            installed   = $true
            owner       = $null
            installPath = $pkg.InstallLocation
            launch      = "shell:AppsFolder\$aumid"
            cover       = $logo
        })
    } catch { Write-Warning "Xbox package $($pkg.Name): $_" }
}

# ------------------------------------------------------------------ Output
$out = Join-Path $here 'games.json'
ConvertTo-Json -InputObject @($games) -Depth 4 | Set-Content -LiteralPath $out -Encoding UTF8

$games | Sort-Object source, title |
    Format-Table source, title, installed, owner, @{ n = 'cover'; e = { [bool]$_.cover } } -AutoSize

$steam = @($games | Where-Object source -eq 'steam')
$xbox  = @($games | Where-Object source -eq 'xbox')
Write-Host ("Steam path : {0}" -f $(if ($steamPath) { $steamPath } else { 'not found' }))
Write-Host ("Libraries  : {0}" -f ($libs -join ' | '))
Write-Host ("Me detected: {0}" -f [bool]$me)
Write-Host ("Steam games: {0} (owner=me: {1}, family: {2}, unknown: {3})" -f $steam.Count,
    @($steam | Where-Object owner -eq 'me').Count,
    @($steam | Where-Object owner -like 'family-*').Count,
    @($steam | Where-Object owner -eq 'unknown').Count)
Write-Host ("Xbox games : {0}" -f $xbox.Count)
Write-Host "Saved      : $out"
