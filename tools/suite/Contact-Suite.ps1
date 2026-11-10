<#
.SYNOPSIS
  Builds one contact-sheet image of a game run: every shot in scenario and step order as a thumbnail, captioned with
  the shot's name and the scenario's status. (contact.html in the run folder has the same in full size with the
  camera and settle details.)

.EXAMPLE
  powershell -File tools/suite/Contact-Suite.ps1 -RunId c3-core
  Writes runs/<RunId>/contact.jpg.
#>
param(
    [Parameter(Mandatory = $true)][string]$RunId,
    [int]$Columns = 6,
    [int]$ThumbWidth = 256,
    [string]$ResultsRoot = $(if ($env:PLACEMAKER_RESULTS_ROOT) { $env:PLACEMAKER_RESULTS_ROOT } else { Join-Path $env:USERPROFILE '.placemaker\suite\runs' })
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$run = Join-Path $ResultsRoot $RunId
$shotsDir = Join-Path $run 'shots'
$summary = Get-Content (Join-Path $run 'summary.json') -Raw | ConvertFrom-Json
$results = @{}
$i = 0
foreach ($r in $summary.scenarios) { $i++; $results[('{0:D2}' -f $i)] = $r }
$files = @(Get-ChildItem $shotsDir -Filter '*.png' | Where-Object { $_.Name -match '^\d\d-' } | Sort-Object Name)
if ($files.Count -eq 0) { Write-Host 'no shots'; exit 1 }
$th = [int]($ThumbWidth * 9 / 16)
$capH = 34
$rows = [math]::Ceiling($files.Count / $Columns)
$W = $Columns * ($ThumbWidth + 6) + 6
$H = $rows * ($th + $capH + 6) + 40
$bmp = New-Object System.Drawing.Bitmap $W, $H
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBilinear
$g.Clear([System.Drawing.Color]::FromArgb(30, 30, 28))
$font = New-Object System.Drawing.Font 'Segoe UI', 8
$bold = New-Object System.Drawing.Font 'Segoe UI', 9, ([System.Drawing.FontStyle]::Bold)
$white = [System.Drawing.Brushes]::Gainsboro
$g.DrawString("Game run $RunId - $($summary.headline)", $bold, $white, 6, 10)
$k = 0
foreach ($f in $files) {
    $col = $k % $Columns; $row = [math]::Floor($k / $Columns)
    $x = 6 + $col * ($ThumbWidth + 6); $y = 40 + $row * ($th + $capH + 6)
    try {
        $img = [System.Drawing.Image]::FromFile($f.FullName)
        $g.DrawImage($img, $x, $y, $ThumbWidth, $th)
        $img.Dispose()
    } catch { }
    $nn = $f.Name.Substring(0, 2)
    $res = $results[$nn]
    $name = $f.BaseName.Substring(3)
    $g.DrawString(("{0} {1}" -f $nn, $name), $font, $white, $x, $y + $th + 2)
    $g.DrawString([string]$res.status, $font, $white, $x, $y + $th + 16)
    $k++
}
$out = Join-Path $run 'contact.jpg'
$codec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object { $_.MimeType -eq 'image/jpeg' }
$ep = New-Object System.Drawing.Imaging.EncoderParameters 1
$ep.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter ([System.Drawing.Imaging.Encoder]::Quality), 82L
$bmp.Save($out, $codec, $ep)
$g.Dispose(); $bmp.Dispose()
Write-Host "contact sheet: $out ($($files.Count) shots)"
