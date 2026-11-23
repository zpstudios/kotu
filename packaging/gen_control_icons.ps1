# Run on Windows with WPF (PresentationCore) and the FontForge CLI on PATH.
# powershell -File packaging/gen_control_icons.ps1
# -OutlinesOnly exports the SVG fonts for inspection in a vector/font editor.
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../src/KOTU.App/Assets'),
    [switch]$OutlinesOnly
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore
$null = New-Item -ItemType Directory -Force -Path $OutputDirectory
$OutputDirectory = (Resolve-Path $OutputDirectory).Path

# Each entry is an original centreline in the same 24-unit square. Keys match
# KOTU's existing FontIcon glyphs, so actions, labels and hit areas stay native.
# An explicit repeat digit and asymmetric arrows keep direction clear at 14 DIP.
$icons = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../src/KOTU.Core/Appearance/IconCenterlines.txt')

function ConvertTo-FontPath([string]$centerline, [Windows.Media.Pen]$pen) {
    $path = [Windows.Media.Geometry]::Parse($centerline)
    $outline = $path.GetWidenedPathGeometry($pen, 0.02, 'Absolute')
    # Union overlapping strokes before reversing the Y axis. This preserves
    # joins, crossed lines and counters on rasterizers that use nonzero winding.
    $outline = [Windows.Media.Geometry]::Combine($outline, [Windows.Media.Geometry]::Empty,
        'Union', $null, 0.02, 'Absolute').GetFlattenedPathGeometry(0.02, 'Absolute')
    $commands = [Text.StringBuilder]::new()
    foreach ($figure in $outline.Figures) {
        $points = @($figure.StartPoint) + @(foreach ($segment in $figure.Segments) {
            if ($segment -is [Windows.Media.PolyLineSegment]) { $segment.Points } else { $segment.Point }
        })
        $first = $true
        foreach ($point in $points) {
            $x = [math]::Round($point.X * 1000 / 24)
            $y = [math]::Round(850 - $point.Y * 1000 / 24)
            $op = if ($first) { 'M' } else { 'L' }
            [void]$commands.Append("$op$x $y ")
            $first = $false
        }
        [void]$commands.Append('Z ')
    }
    $commands.ToString().TrimEnd()
}

foreach ($profile in @(
    @{ Name = 'KotuIcons'; Family = 'KOTU Icons'; Stroke = 1.87; Cap = 'Round' },
    @{ Name = 'KotuIconsThin'; Family = 'KOTU Icons Thin'; Stroke = 1.34; Cap = 'Flat' }
)) {
    $pen = [Windows.Media.Pen]::new([Windows.Media.Brushes]::Black, $profile.Stroke)
    $pen.StartLineCap = $pen.EndLineCap = $profile.Cap
    $pen.LineJoin = if ($profile.Cap -eq 'Round') { 'Round' } else { 'Miter' }
    $pen.MiterLimit = 3
    $font = [Text.StringBuilder]::new()
    [void]$font.AppendLine('<svg xmlns="http://www.w3.org/2000/svg"><defs>')
    [void]$font.AppendLine(('<font id="{0}" horiz-adv-x="1000"><font-face font-family="{1}" units-per-em="1000" ascent="850" descent="150"/><missing-glyph horiz-adv-x="1000"/>' -f $profile.Name, $profile.Family))
    foreach ($line in $icons) {
        $code, $name, $centerline = $line -split ' ',3
        $data = ConvertTo-FontPath $centerline $pen
        [void]$font.AppendLine(('<glyph unicode="&#x{0};" glyph-name="{1}" d="{2}"/>' -f $code, $name, $data))
    }
    [void]$font.AppendLine('</font></defs></svg>')
    $svgDir = if ($OutlinesOnly) { $OutputDirectory } else { [IO.Path]::GetTempPath() }
    $svgPath = Join-Path $svgDir $(if($OutlinesOnly) { $profile.Name + '.svg' } else { [IO.Path]::GetRandomFileName() + '.svg' })
    [IO.File]::WriteAllText($svgPath, $font.ToString(), [Text.UTF8Encoding]::new($false))
    if (!$OutlinesOnly) {
        try {
            $ttfPath = Join-Path $OutputDirectory ($profile.Name + '.ttf')
            & fontforge -lang=ff -c 'Open($1); Generate($2)' $svgPath $ttfPath
            if ($LASTEXITCODE -ne 0) { throw "FontForge export failed for $($profile.Name)." }
        } finally { Remove-Item -LiteralPath $svgPath }
    }
}
