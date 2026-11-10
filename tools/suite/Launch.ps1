# Plain launch for the user: starts CS2 to the main menu with THEIR OWN settings (nothing swapped, no window flags,
# no suite token, no test world) and only local mods: Placemaker plus any -Mods named here (Paradox mods by DLL name,
# copied from the subscribed-mods cache into Mods\ for this session and removed again when the game closes).
#   powershell -File tools/suite/Launch.ps1 -Mods PreservePhotoMode,SunGlasses
#   powershell -File tools/suite/Launch.ps1 -AllMods        # the whole playset, as the launcher would
#   -Window 2560x1440  a window of that size for this launch only (Unity flags; the saved display setting is not edited)
# The graphics settings files are snapshotted before the launch into .forensics/suite/launch-snapshots/<time> so any
# change the game makes to them can be shown and undone.
param([string[]]$Mods = @(), [switch]$AllMods, [string]$Window = '', [switch]$DevMode)
$ud = Join-Path $env:USERPROFILE 'AppData\LocalLow\Colossal Order\Cities Skylines II'
$gameDir = $(if ($env:CS2_GAME_PATH) { $env:CS2_GAME_PATH } else { 'C:\Program Files (x86)\Steam\steamapps\common\Cities Skylines II' })
$exe = Join-Path $gameDir 'Cities2.exe'
if (Get-Process -Name Cities2 -ErrorAction SilentlyContinue) { Write-Host 'STOP: the game is already running'; exit 2 }

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$snap = Join-Path $repoRoot ('.forensics\suite\launch-snapshots\' + (Get-Date -Format 'MMdd-HHmmss'))
New-Item -ItemType Directory -Force -Path $snap | Out-Null
foreach ($f in @('Settings.coc', 'FallbackSettings.coc', 'launcher-settings.json')) { $s = Join-Path $ud $f; if (Test-Path -LiteralPath $s) { Copy-Item -LiteralPath $s -Destination $snap } }
Write-Host "launch: settings snapshot in $snap"
$added = @()
$Mods = @($Mods | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$cache = Join-Path $ud '.cache\Mods\pdx_mods'
foreach ($m in $Mods) {
    $src = Get-ChildItem -LiteralPath $cache -Directory -ErrorAction SilentlyContinue |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName "$m.dll") } |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $src) { Write-Host "STOP: no subscribed mod with $m.dll in $cache"; exit 2 }
    $dst = Join-Path $ud "Mods\$m"
    if (Test-Path -LiteralPath $dst) {
        if (-not (Test-Path -LiteralPath (Join-Path $dst '.playground-copy'))) { Write-Host "STOP: Mods\$m exists and is not a session copy; leaving it alone"; exit 2 }
        Remove-Item -LiteralPath $dst -Recurse -Force
    }
    Copy-Item -LiteralPath $src.FullName -Destination $dst -Recurse
    Set-Content -LiteralPath (Join-Path $dst '.playground-copy') -Value "copied from $($src.FullName) for a plain launch" -Encoding ASCII
    $added += $dst
    Write-Host "launch: loading $m from $($src.Name) as a local mod for this session"
}

$code = 0
try {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.Arguments = $(if ($AllMods) { '' } else { '-disableModding -disableCodeModding-' })
    if ($Window -match '^(\d+)x(\d+)$') { $psi.Arguments = ("$($psi.Arguments) -screen-fullscreen 0 -screen-width $($Matches[1]) -screen-height $($Matches[2])").Trim() }
    if ($DevMode) { $psi.Arguments = ("$($psi.Arguments) -developerMode").Trim() }
    $psi.WorkingDirectory = $gameDir
    $psi.UseShellExecute = $false
    $psi.EnvironmentVariables['SteamAppId'] = '949230'
    $psi.EnvironmentVariables['SteamGameId'] = '949230'
    $p = [System.Diagnostics.Process]::Start($psi)
    Write-Host "launch: started Cities2.exe PID $($p.Id) $($psi.Arguments) (the user's own settings; main menu)"
    $p.WaitForExit()
    Write-Host 'launch: the game closed'
}
catch { Write-Host "launch: $($_.Exception.Message)"; $code = 1 }
finally {
    foreach ($d in $added) {
        if (Test-Path -LiteralPath (Join-Path $d '.playground-copy')) { Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction SilentlyContinue; Write-Host "launch: removed the session copy $d" }
    }
}
exit $code
