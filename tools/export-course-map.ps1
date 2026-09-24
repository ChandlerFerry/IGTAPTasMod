param(
    [Parameter(Mandatory = $true)][string]$ModelPath,
    [string]$Course = 'course 2',
    [string]$OutputDirectory = '.\obj\course-map'
)

$ErrorActionPreference = 'Stop'
$model = Get-Content -LiteralPath $ModelPath -Raw | ConvertFrom-Json
$coursePrefix = "Courses/$Course/"
$viewportCollider = $model.colliders | Where-Object { $_.name -ieq "$coursePrefix`Clones/activeCloneArea" } | Select-Object -First 1
if (-not $viewportCollider) { throw "Could not find the activeCloneArea for $Course." }

$viewport = [ordered]@{
    minX = [double]$viewportCollider.centerX - [double]$viewportCollider.sizeX / 2
    maxX = [double]$viewportCollider.centerX + [double]$viewportCollider.sizeX / 2
    minY = [double]$viewportCollider.centerY - [double]$viewportCollider.sizeY / 2
    maxY = [double]$viewportCollider.centerY + [double]$viewportCollider.sizeY / 2
}

function Test-Overlap($bounds, $area) {
    return $bounds.maxX -ge $area.minX -and $bounds.minX -le $area.maxX -and
        $bounds.maxY -ge $area.minY -and $bounds.minY -le $area.maxY
}

function Get-Kind($collider) {
    if ($collider.name -match 'BallKiller') { return 'killer' }
    if ($collider.name -match 'Spike') { return 'spike' }
    if ($collider.name -match '/Checkpoint') { return 'checkpoint' }
    if ($collider.name -match '/Start$') { return 'start' }
    if ($collider.name -match '/RealEnd$') { return 'finish' }
    if (-not $collider.trigger -and ($collider.tag -eq 'Ground' -or $collider.tag -eq 'Moss')) { return 'solid' }
    if ($collider.trigger) { return 'trigger' }
    return 'other'
}

$features = [System.Collections.Generic.List[object]]::new()
foreach ($collider in $model.colliders) {
    $belongsToCourse = $collider.name.StartsWith($coursePrefix, [System.StringComparison]::OrdinalIgnoreCase)
    $isSharedGeometry = $collider.name.StartsWith('Grid/', [System.StringComparison]::OrdinalIgnoreCase)
    if (-not $belongsToCourse -and -not $isSharedGeometry) { continue }

    $kind = Get-Kind $collider
    if ($isSharedGeometry -and $kind -notin @('solid', 'spike')) { continue }
    $points = @($collider.points)
    $starts = @($collider.pathStarts)
    if ($points.Count -ge 6 -and $starts.Count -gt 0) {
        $pathEnds = @($starts) + @([int]($points.Count / 2))
        for ($pathIndex = 0; $pathIndex -lt $starts.Count; $pathIndex++) {
            $path = [System.Collections.Generic.List[object]]::new()
            for ($pointIndex = [int]$pathEnds[$pathIndex]; $pointIndex -lt [int]$pathEnds[$pathIndex + 1]; $pointIndex++) {
                $path.Add([ordered]@{ x = [double]$points[$pointIndex * 2]; y = [double]$points[$pointIndex * 2 + 1] })
            }
            $xs = @($path | ForEach-Object x); $ys = @($path | ForEach-Object y)
            $bounds = [ordered]@{
                minX = ($xs | Measure-Object -Minimum).Minimum; maxX = ($xs | Measure-Object -Maximum).Maximum
                minY = ($ys | Measure-Object -Minimum).Minimum; maxY = ($ys | Measure-Object -Maximum).Maximum
            }
            if (Test-Overlap $bounds $viewport) {
                $features.Add([ordered]@{ name = $collider.name; kind = $kind; tag = $collider.tag
                    trigger = [bool]$collider.trigger; pathIndex = $pathIndex; bounds = $bounds; points = $path })
            }
        }
    }
    elseif ($belongsToCourse -and [double]$collider.sizeX -gt 0 -and [double]$collider.sizeY -gt 0) {
        $bounds = [ordered]@{
            minX = [double]$collider.centerX - [double]$collider.sizeX / 2
            maxX = [double]$collider.centerX + [double]$collider.sizeX / 2
            minY = [double]$collider.centerY - [double]$collider.sizeY / 2
            maxY = [double]$collider.centerY + [double]$collider.sizeY / 2
        }
        if (Test-Overlap $bounds $viewport) {
            $features.Add([ordered]@{ name = $collider.name; kind = $kind; tag = $collider.tag
                trigger = [bool]$collider.trigger; pathIndex = 0; bounds = $bounds; points = @() })
        }
    }
}

$orderedFeatures = @($features | Sort-Object @{ Expression = { @('solid','spike','killer','checkpoint','start','finish','trigger','other').IndexOf($_.kind) } }, name, pathIndex)
$summary = [ordered]@{}
foreach ($group in ($orderedFeatures | Group-Object -Property { $_['kind'] })) { $summary[$group.Name] = $group.Count }
$dump = [ordered]@{
    schemaVersion = 1
    generatedUtc = [DateTime]::UtcNow.ToString('o')
    sourceModel = (Resolve-Path -LiteralPath $ModelPath).Path
    course = $Course
    player = [ordered]@{ x = [double]$model.playerX; y = [double]$model.playerY }
    viewport = $viewport
    summary = $summary
    features = $orderedFeatures
}

$resolvedOutput = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($resolvedOutput) | Out-Null
$jsonPath = Join-Path $resolvedOutput ($Course.Replace(' ', '-') + '-map.json')
$svgPath = Join-Path $resolvedOutput ($Course.Replace(' ', '-') + '-map.svg')
$dump | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $jsonPath -Encoding utf8

$width = $viewport.maxX - $viewport.minX; $height = $viewport.maxY - $viewport.minY
$style = @{
    solid = 'fill:#374151;stroke:#9ca3af;stroke-width:2'; spike = 'fill:#dc2626;stroke:#fecaca;stroke-width:2'
    killer = 'fill:#7c2d12;stroke:#fb923c;stroke-width:3'; checkpoint = 'fill:#2563eb;fill-opacity:.22;stroke:#60a5fa;stroke-width:3'
    start = 'fill:#16a34a;fill-opacity:.3;stroke:#4ade80;stroke-width:4'; finish = 'fill:#f59e0b;fill-opacity:.5;stroke:#fde047;stroke-width:4'
    trigger = 'fill:#7c3aed;fill-opacity:.08;stroke:#a78bfa;stroke-width:1'; other = 'fill:none;stroke:#64748b;stroke-width:1'
}
$svg = [Text.StringBuilder]::new()
[void]$svg.AppendLine("<svg xmlns=`"http://www.w3.org/2000/svg`" viewBox=`"0 0 $width $height`" width=`"1400`" height=`"1027`">")
[void]$svg.AppendLine('<rect width="100%" height="100%" fill="#0f172a"/>')
foreach ($feature in $orderedFeatures) {
    $featureStyle = $style[$feature.kind]
    $title = [Security.SecurityElement]::Escape([string]$feature.name)
    if (@($feature.points).Count -gt 0) {
        $coordinates = @($feature.points | ForEach-Object { "$(($_.x - $viewport.minX).ToString('0.###',[Globalization.CultureInfo]::InvariantCulture)),$((($viewport.maxY - $_.y)).ToString('0.###',[Globalization.CultureInfo]::InvariantCulture))" }) -join ' '
        [void]$svg.AppendLine("<polygon points=`"$coordinates`" style=`"$featureStyle`"><title>$title</title></polygon>")
    } else {
        $x = $feature.bounds.minX - $viewport.minX; $y = $viewport.maxY - $feature.bounds.maxY
        $w = $feature.bounds.maxX - $feature.bounds.minX; $h = $feature.bounds.maxY - $feature.bounds.minY
        [void]$svg.AppendLine("<rect x=`"$x`" y=`"$y`" width=`"$w`" height=`"$h`" style=`"$featureStyle`"><title>$title</title></rect>")
    }
}
[void]$svg.AppendLine('</svg>')
$svg.ToString() | Set-Content -LiteralPath $svgPath -Encoding utf8

[pscustomobject]@{ json = $jsonPath; svg = $svgPath; featureCount = $orderedFeatures.Count; summary = $summary }
