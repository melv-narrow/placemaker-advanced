<#
.SYNOPSIS
  See builds in the game: pick category, type(s) and style(s); get pictures of each finished build and of the plan it
  was built from, and where each build is in the log. One game launch for all of them.

.EXAMPLE
  powershell -File tools/suite/Show.ps1                                          # list categories, types, styles
  powershell -File tools/suite/Show.ps1 -Category parks                          # the types and styles of a category
  powershell -File tools/suite/Show.ps1 -Category parks -Type park -Style formal # the usual: 3 pictures
  powershell -File tools/suite/Show.ps1 -Category parking -Type parking -Style all -Views top -Modes clean,zoning
  powershell -File tools/suite/Show.ps1 -Category shops -Type all -Views 45,top -NoPlan
  powershell -File tools/suite/Show.ps1 -Type park -Shape circle -Size 140 -Ground hills
  powershell -File tools/suite/Show.ps1 -Type parking -Shape pinch -Size 180x110 -Ground slope -Grade 8 -Toward east
  powershell -File tools/suite/Show.ps1 -Type roads -Size 900x700 -Ground mountains -Roads none -Views top
  powershell -File tools/suite/Show.ps1 -Type plaza -Land '{"points":[[0,0],[90,0],[90,40],[30,70],[0,60]],"ground":"bumpy"}'

.DESCRIPTION
  Goes through the machine-wide game queue (suite/README.md, "The game queue"): it waits its turn, printing its place in
  line, who is ahead and an ETA; the queue's game builds each one on its own test land the way a person does (select
  the area, pick the type and style, entrances, Review, Build). Run it as a background command: it exits when the
  results are back. A new game takes about 60 s to start, then about 10-15 s per build; a game already open with the
  same build is reused. -Direct launches the game here and now instead (debugging; the game must be closed).

  Land. Without land options each build gets a flat 150 x 140 m city block of the test grid (-Site for another kind).
  With any of them each build gets its own land, made to order before the roads go in:
    -Shape      rect (default), circle, pinch (a waist in the middle; -Land neck), l, or -Land points (any outline)
    -Size       W x D metres ("180x110"), or one number for both; 4 to 4000 m
    -Ground     flat (default: perfectly flat), slope (-Grade % or 0.06, -Toward north|south|east|west),
                hills, bumpy, mountains (-Amplitude m, -Wavelength m), or your own heights: -Heights a JSON file
                ([[m, m, ...], ...] rows north to south) or -Heightmap a greyscale PNG (-Range m for white)
    -Roads      around (default: a ring road, so entrances have a street) or none (open land, e.g. to test roads)
    -Land       the same as JSON, merged over the options (keys: suite/schema.json "land")
  The area is drawn with the Points tool in the land's shape.

  Pictures. Start with the default three; ask for more only when they did not show what you needed:
    plan              the build's plan in Review before building (first view, overlay: the outline and the ghost)
    <view>-<mode>     the finished build, for every view x every mode asked (default 45-clean)
    terrain           the ground's height map (default)
  Views: 45 (default; 45 degrees from the south), top (straight down), low (20 degrees), close (a close look at the
  middle). Every view but close is framed on the build.
  Modes: clean (default; no overlays), zoning (the game's zone grid: zoned cells in their zone colour), overlay
  (Placemaker's and the game's overlays), ui (the real screen, with the game's bars), terrain (a map of the ground's
  height from above, once per build whatever the views: blue below the outline's typical ground, red above, contour
  lines every 0.5 m, the outline in white; no buildings, trees or roads).

  Logs: Placemaker.log and the lines of each build; Player.log for the game's own errors; suite.log for the runner's
  steps with timings. Everything is in the run folder it prints; contact.html shows every picture in order.
#>
param(
    [string]$Category = '',
    [string[]]$Type = @(),
    [string[]]$Style = @(),
    [string[]]$Views = @('45'),
    [string[]]$Modes = @('clean', 'terrain'),
    [switch]$NoPlan,
    # grow the city this many seconds after the build, before the pictures (shops on zoned plots; 0 = no grow)
    [int]$Grow = 0,
    # grow only after the builds of these types (default: every build)
    [string[]]$GrowTypes = @(),
    # a grid block: flat | slope | rough | zoned (housing grown next to it) | survive (two plopped parks on the block)
    [ValidateSet('flat', 'slope', 'rough', 'zoned', 'survive')][string]$Site = 'flat',
    # land made to order (see the description)
    [string]$Shape = '',
    [string]$Size = '',
    [string]$Ground = '',
    [double]$Grade = 0,
    [string]$Toward = '',
    [double]$Amplitude = 0,
    [double]$Wavelength = 0,
    [string]$Roads = '',
    [string]$Heights = '',
    [string]$Heightmap = '',
    [double]$Range = 0,
    [string]$Land = '',
    # several lands in one request: a JSON array (a file or the text) of land objects, each with a "name" (the build ids get
    # -<name>); {"name":"block"} alone is the grid block. Every build is made on every land, e.g.
    # [{"name":"block"},{"name":"big","size":[300,280]},{"name":"long","size":[360,110]},{"name":"l","shape":"l","size":[280,260]}]
    # A land with "only": ["stripMall"] is used for those types only.
    [string]$Lands = '',
    [switch]$AllMods,
    [switch]$NoBuild,
    [string]$ResultsRoot = $(if ($env:PLACEMAKER_RESULTS_ROOT) { $env:PLACEMAKER_RESULTS_ROOT } else { Join-Path $env:USERPROFILE '.placemaker\suite\runs' }),
    # the game queue (default): waits its turn in the machine-wide line, printing its place and an ETA; -Direct launches now
    [switch]$Direct,
    [string]$Who = ''
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
function Split-List([string[]]$a) { @($a | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }
$Type = Split-List $Type; $Style = Split-List $Style; $Views = Split-List $Views; $Modes = Split-List $Modes

# The Make row's categories: the central shelf (Recipes/shelf/<category>.json, Core/Tool/Shelf.cs), one file per category, in
# their order; each names the type groups it shows, types moved in from another group, and the types and styles it hides.
# Hidden types and styles are marked in the lists, left out of -Type all and -Style all, and can still be built by id.
$shelfDir = Join-Path $repo 'Recipes\shelf'
$shelf = @(Get-ChildItem -LiteralPath $shelfDir -Filter '*.json' -ErrorAction SilentlyContinue | ForEach-Object {
        $o = Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
        if (-not $o.id) { $o | Add-Member -NotePropertyName id -NotePropertyValue $_.BaseName -Force }
        if ($null -eq $o.order) { $o | Add-Member -NotePropertyName order -NotePropertyValue 1000 -Force }
        $o
    } | Sort-Object { [int]$_.order }, id)
$categories = [ordered]@{}
foreach ($c in $shelf) { if (-not $categories.Contains($c.id)) { $categories[$c.id] = @($c.groups) } }
$aliases = @{ 'shops & squares' = 'shops'; 'shops&squares' = 'shops'; 'commercial' = 'shops'; 'squares' = 'shops'; 'park' = 'parks'; 'gardens' = 'parks'; 'road' = 'roads'; 'water' = 'beach'; 'industrial' = 'industry' }
$viewSteps = @{
    '45'  = [ordered]@{ view = 'oblique'; pitch = 45 }
    'top' = [ordered]@{ view = 'top' }
    'low' = [ordered]@{ view = 'low' }
    'close' = [ordered]@{ view = 'closeup' }
}
$modeSteps = @{ clean = @('render', $false); zoning = @('render', $true); overlay = @('overlay', $false); ui = @('ui', $false); terrain = $null }
$catalog = (Get-Content (Join-Path $repo 'suite\catalog.json') -Raw | ConvertFrom-Json).types
function ShelfOf([string]$id) { $shelf | Where-Object { $_.id -eq $id } | Select-Object -First 1 }
function CategoryOf($t) {
    foreach ($c in $shelf) { if (@($c.types) -contains $t.layer) { return $c.id } }
    foreach ($c in $shelf) { if (@($c.groups) -contains $t.group) { return $c.id } }
    return $null
}
function TypeHidden($t) { $c = ShelfOf (CategoryOf $t); return [bool]($c -and ($c.hidden -or (@($c.hiddenTypes) -contains $t.layer))) }
function HiddenStyles($t) { $c = ShelfOf (CategoryOf $t); if ($c -and $c.hiddenStyles -and $c.hiddenStyles.PSObject.Properties[$t.layer]) { return @($c.hiddenStyles.($t.layer)) }; return @() }
function TypesOf([string]$k) { @($catalog | Where-Object { (CategoryOf $_) -eq $k }) }
function ListCategory($k) {
    $c = ShelfOf $k
    Write-Host ("{0}:{1}" -f $k, $(if ($c -and $c.hidden) { ' (hidden from the Make row; types can still be built by id)' } else { '' }))
    foreach ($t in TypesOf $k) {
        $hs = HiddenStyles $t
        $shown = @($t.presets | Where-Object { $hs -notcontains $_ })
        Write-Host ("  {0,-20}{1} styles: {2}{3}" -f $t.layer, $(if (TypeHidden $t) { ' (hidden)' } else { '' }), ($shown -join ', '), $(if ($hs.Count) { "; hidden: $($hs -join ', ')" } else { '' }))
    }
}
function Stop-With([string]$why) { Write-Host $why; exit 2 }

$cat = $Category.Trim().ToLowerInvariant()
if ($aliases.ContainsKey($cat)) { $cat = $aliases[$cat] }
if ($cat -and -not $categories.Contains($cat)) { Stop-With "No category '$Category'. Categories: $(@($categories.Keys) -join ', ')" }
if ($Type.Count -eq 0) {
    if ($cat) { ListCategory $cat } else { foreach ($k in $categories.Keys) { ListCategory $k } }
    Write-Host "`nThen: Show.ps1 -Category <category> -Type <type> -Style <style>"
    exit 0
}
foreach ($v in $Views) { if (-not $viewSteps.ContainsKey($v)) { Stop-With "No view '$v'. Views: 45, top, low, close" } }
foreach ($m in $Modes) { if (-not $modeSteps.ContainsKey($m)) { Stop-With "No mode '$m'. Modes: clean, zoning, overlay, ui, terrain" } }
$viewModes = @($Modes | Where-Object { $_ -ne 'terrain' })
$terrainMap = $Modes -contains 'terrain'

# â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€ land made to order â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
$landSpec = $null
if ($Shape -or $Size -or $Ground -or $Grade -or $Toward -or $Amplitude -or $Wavelength -or $Roads -or $Heights -or $Heightmap -or $Land) {
    $landSpec = [ordered]@{}
    if ($Shape) { $landSpec['shape'] = $Shape.ToLowerInvariant() }
    if ($Size) {
        if ($Size -match '^\s*([0-9.]+)\s*[xX*]\s*([0-9.]+)\s*$') { $landSpec['size'] = @([double]$Matches[1], [double]$Matches[2]) }
        elseif ($Size -match '^\s*[0-9.]+\s*$') { $landSpec['size'] = [double]$Size }
        else { Stop-With "-Size is 'W x D' metres or one number, not '$Size'" }
    }
    if ($Ground) { $landSpec['ground'] = $Ground.ToLowerInvariant() }
    if ($Grade) { $landSpec['grade'] = $Grade }
    if ($Toward) { $landSpec['toward'] = $Toward.ToLowerInvariant() }
    if ($Amplitude) { $landSpec['amplitude'] = $Amplitude }
    if ($Wavelength) { $landSpec['wavelength'] = $Wavelength }
    if ($Roads) { $landSpec['roads'] = $Roads.ToLowerInvariant() }
    if ($Heights) { if (-not (Test-Path $Heights)) { Stop-With "No heights file $Heights" }; $landSpec['heights'] = (Get-Content $Heights -Raw | ConvertFrom-Json) }
    if ($Heightmap) { if (-not (Test-Path $Heightmap)) { Stop-With "No heightmap $Heightmap" }; $landSpec['heightmap'] = (Resolve-Path $Heightmap).Path; if ($Range) { $landSpec['range'] = $Range } }
    if ($Land) {
        $text = if (Test-Path -LiteralPath $Land -PathType Leaf) { Get-Content -LiteralPath $Land -Raw } else { $Land }
        try { $extra = $text | ConvertFrom-Json } catch { Stop-With "-Land is not JSON: $text (give a .json file; Windows PowerShell drops the quotes inside -Land '{...}')" }
        foreach ($p in $extra.PSObject.Properties) { $landSpec[$p.Name] = $p.Value }
    }
    $schema = Get-Content (Join-Path $repo 'suite\schema.json') -Raw | ConvertFrom-Json
    foreach ($k in @($landSpec.Keys)) { if (-not (@($schema.land.keys) -contains $k)) { Stop-With "land: unknown key '$k' (keys: $(@($schema.land.keys) -join ', '))" } }
    foreach ($e in $schema.land.enums.PSObject.Properties) {
        $v = $landSpec[$e.Name]
        if ($v -is [string] -and -not (@($e.Value) | Where-Object { $_ -ieq $v })) { Stop-With "land: '$($e.Name)' must be one of $(@($e.Value) -join ', '), not '$v'" }
    }
}

# â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€ the builds: every type x every style â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
$types = @()
if ($Type.Count -eq 1 -and $Type[0] -ieq 'all') {
    if (-not $cat) { Stop-With '-Type all needs a -Category.' }
    $types = @(TypesOf $cat | Where-Object { -not (TypeHidden $_) })   # the shelf's hidden types only by name
} else {
    foreach ($name in $Type) {
        $t = $catalog | Where-Object { $_.layer -ieq $name } | Select-Object -First 1
        if (-not $t) { Write-Host "No type '$name'."; if ($cat) { ListCategory $cat } else { Write-Host 'Run Show.ps1 with no arguments for the list.' }; exit 2 }
        $tcat = CategoryOf $t
        if (-not $tcat) { Stop-With "'$($t.layer)' is in no category of Recipes/shelf (group $($t.group))." }
        if (TypeHidden $t) { Write-Host "Note: '$($t.layer)' is hidden from the Make row by Recipes/shelf/$tcat.json; building it by id." }
        if ($cat -and $tcat -ne $cat) { Write-Host "'$($t.layer)' is in $tcat, not $cat."; ListCategory $tcat; exit 2 }
        $types += $t
    }
}
# (rf-shops3) Made-to-order land touches its street the way a user draws it: the outline about 2 m off the kerb of the
# 16 m ring road (gap 10 from the shape to the road centre; the old default, 16 m, left the outline 8 m off the kerb, out
# of every type's frontage reach), and round or odd shapes get a ring road that follows them. Give "gap" (or roads
# "around") on purpose for a site that does not touch its street.
function Default-Land($spec) {
    if (-not $spec) { return $spec }
    if (-not $spec.Contains('gap')) { $spec['gap'] = 10 }
    $shape = "$($spec['shape'])".ToLowerInvariant()
    if (-not $spec.Contains('roads') -and (@('l', 'circle', 'pinch', 'points') -contains $shape)) { $spec['roads'] = 'follow' }
    return $spec
}
$landSpec = Default-Land $landSpec
$landList = @([pscustomobject]@{ Name = ''; Spec = $landSpec })
if ($Lands) {
    $text = if (Test-Path -LiteralPath $Lands -PathType Leaf) { Get-Content -LiteralPath $Lands -Raw } else { $Lands }
    try { $arr = @($text | ConvertFrom-Json | ForEach-Object { $_ }) } catch { Stop-With "-Lands is not a JSON array: $text" }
    $schema = Get-Content (Join-Path $repo 'suite\schema.json') -Raw | ConvertFrom-Json
    $landList = @(foreach ($o in $arr) {
            $spec = [ordered]@{}
            if ($landSpec) { foreach ($k in $landSpec.Keys) { $spec[$k] = $landSpec[$k] } }
            foreach ($p in $o.PSObject.Properties) {
                if ($p.Name -eq 'name' -or $p.Name -eq 'only') { continue }
                if (-not (@($schema.land.keys) -contains $p.Name)) { Stop-With "-Lands: unknown key '$($p.Name)' (keys: $(@($schema.land.keys) -join ', '))" }
                $spec[$p.Name] = $p.Value
            }
            [pscustomobject]@{ Name = "$($o.name)"; Spec = $(if ($spec.Count) { Default-Land $spec } else { $null }); Only = @($o.only | Where-Object { $_ }) }
        })
}
$builds = @()
foreach ($t in $types) {
    $hiddenStyles = HiddenStyles $t
    # 'type:style' names a style of one type only (several types with their own styles in one request)
    $mine = @($Style | Where-Object { $_ -notlike '*:*' -or $_ -like "$($t.layer):*" } | ForEach-Object { if ($_ -like '*:*') { $_.Split(':')[1] } else { $_ } })
    if ($Style.Count -gt 0 -and $mine.Count -eq 0) { $mine = @('') }
    $styles = if ($Style.Count -eq 0) { @('') } elseif ($mine.Count -eq 1 -and $mine[0] -ieq 'all') { @('') + @($t.presets | Where-Object { $hiddenStyles -notcontains $_ }) } else { $mine }
    foreach ($s in $styles) {
        $preset = if ($s -and $s -ine 'default') { @($t.presets) | Where-Object { $_ -ieq $s } | Select-Object -First 1 } else { '' }
        if ($s -and $s -ine 'default' -and -not $preset) { Stop-With "'$($t.layer)' has no style '$s'. Styles: default, $(@($t.presets) -join ', ')" }
        if ($preset -and $hiddenStyles -contains $preset -and -not ($Style.Count -eq 1 -and $Style[0] -ieq 'all')) { Write-Host "Note: style '$preset' of '$($t.layer)' is hidden by Recipes/shelf/$(CategoryOf $t).json; building it by id." }
        foreach ($ln in $landList) {
            if ($ln.Only -and $ln.Only.Count -gt 0 -and -not ($ln.Only -contains $t.layer)) { continue }   # "only": [types] in -Lands
            $builds += [pscustomobject]@{ Layer = $t.layer; Category = (CategoryOf $t); Preset = $preset; Land = $ln.Spec
                Id = $t.layer + '-' + $(if ($preset) { $preset } else { 'default' }) + $(if ($ln.Name) { '-' + $ln.Name } else { '' }) }
        }
    }
}

$first = $viewSteps[$Views[0]]
$scenarios = foreach ($b in $builds) {
    $steps = @(
        [ordered]@{ do = 'area'; tool = $(if ($b.Land) { 'land' } else { 'bucket' }) }
        [ordered]@{ do = 'type'; layer = $b.Layer; preset = $b.Preset }
        [ordered]@{ do = 'entrances' }
    )
    $planCam = [ordered]@{ target = 'build' }; foreach ($k in $first.Keys) { $planCam[$k] = $first[$k] }
    if ($NoPlan) { $steps += [ordered]@{ do = 'preview' } }
    else { $steps += [ordered]@{ do = 'preview'; shot = 'plan'; shotMode = 'overlay'; camera = $planCam } }
    $steps += [ordered]@{ do = 'build' }
    if ($Grow -gt 0 -and ($GrowTypes.Count -eq 0 -or (@($GrowTypes | ForEach-Object { $_ -split ',' }) -contains $b.Layer))) { $steps += [ordered]@{ do = 'grow'; seconds = $Grow } }
    foreach ($v in $Views) {
        foreach ($m in $viewModes) {
            $st = [ordered]@{ do = 'camera'; target = 'build' }
            foreach ($k in $viewSteps[$v].Keys) { $st[$k] = $viewSteps[$v][$k] }
            $st['shot'] = "$v-$m"
            $st['shotMode'] = $modeSteps[$m][0]
            if ($modeSteps[$m][1]) { $st['zones'] = $true }
            $steps += $st
        }
    }
    if ($terrainMap) { $steps += [ordered]@{ do = 'terrain'; name = 'terrain' } }
    $sc = [ordered]@{ id = $b.Id; site = $Site; pace = 'settled'; steps = $steps }
    if ($b.Land) { $sc['land'] = $b.Land }
    $sc
}

$label = if ($builds.Count -eq 1) { $builds[0].Id } else { "$($builds.Count)-builds" }
$runId = 'show-' + $label + '-' + (Get-Date -Format 'MMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 4)   # two agents asking the same in the same second get two folders
$setDir = Join-Path (Split-Path $ResultsRoot) 'show'
New-Item -ItemType Directory -Force -Path $setDir | Out-Null
$setFile = Join-Path $setDir "$runId.json"
[IO.File]::WriteAllText($setFile, ([ordered]@{ scenarios = @($scenarios) } | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding $false))

$perBuild = (@($Views).Count * $viewModes.Count) + $(if ($terrainMap) { 1 } else { 0 }) + $(if ($NoPlan) { 0 } else { 1 })
$where = if ($landSpec) { "on land " + ($landSpec | ConvertTo-Json -Compress -Depth 3) } else { "on a $Site grid block" }
$about = if ($Direct) { "about $([math]::Round(60 + $builds.Count * (8 + 4.5 * $perBuild))) s" } else { 'the queue prints its ETA' }
Write-Host ("Building {0} in the game {1}: {2}; {3} picture(s) each ({4})..." -f $builds.Count, $where, (($builds | ForEach-Object { $_.Id }) -join ', '), $perBuild, $about)
$runArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'Run-Suite.ps1'), '-Set', $setFile, '-RunId', $runId)
if ($AllMods) { $runArgs += '-AllMods' }
if ($NoBuild) { $runArgs += '-NoBuild' }
if ($Direct) { $runArgs += '-Direct' }
if ($Who) { $runArgs += @('-Who', $Who) }
$ErrorActionPreference = 'Continue'   # the game writes to stderr; that is not a failure
$hostOut = & powershell @runArgs 2>&1 | ForEach-Object { $l = "$_"; if ($l -like 'queue: *' -or $l -like 'STOP*') { Write-Host $l }; $l }   # the queue's place-in-line and ETA lines as they come
$runCode = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
$runDir = Join-Path $ResultsRoot $runId

# â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€ what to look at â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
$summaryFile = Join-Path $runDir 'summary.json'
if (-not (Test-Path -LiteralPath $summaryFile)) {
    Write-Host 'The run did not get as far as the game. Last lines:'
    $hostOut | Select-Object -Last 15 | ForEach-Object { Write-Host "  $_" }
    if ($runCode -eq 2) { Remove-Item -LiteralPath $setFile -Force -ErrorAction SilentlyContinue; exit 2 }   # left the line (or a usage error)
    exit 1
}
$rows = @((Get-Content -LiteralPath $summaryFile -Raw -Encoding UTF8 | ConvertFrom-Json).scenarios)
$log = Join-Path $runDir 'logs\Placemaker.log'
$lines = if (Test-Path -LiteralPath $log) { @(Get-Content -LiteralPath $log -Encoding UTF8) } else { @() }
$pictures = 0
Write-Host ''
$k = 0
foreach ($sc in $rows) {
    $k++
    $from = $null; $to = $null
    $logId = if ($sc.sessionId) { $sc.sessionId } else { $sc.id }   # the queue's game names a repeated id 'id+' in its log
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if (-not $from -and $lines[$i] -like "*Placemaker suite: scenario $logId on *") { $from = $i + 1 }
        if ($lines[$i] -like "*Placemaker suite: scenario ${logId}: *") { $to = $i + 1 }
    }
    $why = if ($sc.status -eq 'built') { '' } elseif ($sc.failure) { ": $($sc.failure)" } elseif ($sc.unready) { ": $($sc.unready)" } elseif ($sc.refused) { ": $($sc.refused)" } else { '' }
    Write-Host ("{0}: {1}{2} ({3})" -f $sc.id, $sc.status, $why, $sc.block)
    if ($sc.shots) {
        foreach ($p in $sc.shots.PSObject.Properties) {
            Write-Host ("  {0,-14} {1}" -f $p.Name, (Join-Path $runDir $p.Value))
            if ("$($p.Value)" -notlike 'ERROR*') { $pictures++ }
        }
    }
    if ($terrainMap) {
        $scFile = Get-ChildItem -LiteralPath (Join-Path $runDir 'scenarios') -Filter ('{0:D2}-*.json' -f $k) -ErrorAction SilentlyContinue | Select-Object -First 1
        $tm = if ($scFile) { @((Get-Content $scFile.FullName -Raw -Encoding UTF8 | ConvertFrom-Json).steps) | Where-Object { $_.do -eq 'terrain' -and $_.terrain } | Select-Object -First 1 } else { $null }
        if ($tm) { Write-Host ("  {0,-14} ground {1} m to +{2} m around its typical level; colours +/-{3} m; contours every {4} m" -f 'heights', $tm.terrain.minM, $tm.terrain.maxM, $tm.terrain.rangeM, $tm.terrain.contourM) }
    }
    # a build the game was in the middle of when it went away has no end line: its lines run to the end of the log
    Write-Host ("  {0,-14} {1}{2}" -f 'log', $log, $(if ($from -and $to) { ", lines $from-$to" } elseif ($from) { ", lines $from-$($lines.Count) (the end of the log: the game stopped during this build)" } else { '' }))
}
$never = @($builds | Select-Object -Skip $rows.Count | ForEach-Object { $_.Id })
if ($never.Count -gt 0) { Write-Host ''; Write-Host ("Never ran ({0} of {1}): {2}" -f $never.Count, $builds.Count, ($never -join ', ')) }
Write-Host ''
Write-Host "Game errors:    $(Join-Path $runDir 'logs\Player.log')"
Write-Host "Runner steps:   $(Join-Path $runDir 'suite.log')"
Write-Host "All pictures:   $(Join-Path $runDir 'contact.html')"
# a run that did not finish (the game crashed or was stopped) fails, even with some pictures
if ($runCode -ne 0) { Write-Host "The run did not finish (exit $runCode): read the lines above and logs\Player.log."; exit 1 }
if ($pictures -gt 0) { exit 0 } else { exit 1 }
