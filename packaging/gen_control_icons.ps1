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
$icons = @"
E000 Equalizer M5 2 V22 M12 2 V22 M19 2 V22 M2.5 15 H7.5 M9.5 6 H14.5 M16.5 10.5 H21.5
E001 Analyzer M3 21 V14 M7.5 21 V8 M12 21 V3 M16.5 21 V12.5 M21 21 V6
E002 CollapseSidebars M2 4.5 H22 V19.5 H2 Z M5 5 V19 M19 5 V19 M8.5 9 H15.5 V15 H8.5 Z
E003 ExitFullscreen M8.5 3 H21 V16 H17.5 M3 8 H16 V21 H3 Z
E004 RepeatOff M3 12 V8 Q3 6 5 6 H5.8 M9.5 6 H19 M16 3 L19.4 6 16 9 M21 12 V16 Q21 17 20.1 17.6 M16.5 18 H5 M8 15 L4.6 18 8 21 M2.5 2.5 L21.5 21.5
E700 Menu M3 5.5 H21 M3 12 H21 M3 18.5 H21
E70D Down M5 9 L12 15.4 19 9
E70E Up M5 15 L12 8.6 19 15
E70F Edit M3.5 20.5 L4.5 15 16.8 2.7 Q17.7 1.8 18.7 2.7 L21 5 Q22 6 21 7 L9 19.3 Z M14.8 4.9 L18.8 8.9
E712 More M4.5 12 A.5 .5 0 1 1 3.5 12 A.5 .5 0 1 1 4.5 12 Z M12.5 12 A.5 .5 0 1 1 11.5 12 A.5 .5 0 1 1 12.5 12 Z M20.5 12 A.5 .5 0 1 1 19.5 12 A.5 .5 0 1 1 20.5 12 Z
E713 Settings M9.7 2 H14.3 L14.9 4.8 17.1 5.8 19.6 4.3 21.7 8.1 19.6 10.1 V13.8 L21.7 15.9 19.6 19.7 17.1 18.2 14.9 19.2 14.3 22 H9.7 L9.1 19.2 6.9 18.2 4.4 19.7 2.3 15.9 4.4 13.8 V10.1 L2.3 8.1 4.4 4.3 6.9 5.8 9.1 4.8 Z M15.3 12 A3.3 3.3 0 1 1 8.7 12 A3.3 3.3 0 1 1 15.3 12 Z
E714 Video M3 6 H14 Q15.5 6 15.5 7.5 V16.5 Q15.5 18 14 18 H3 Q2 18 2 17 V7 Q2 6 3 6 Z M15.5 10 L21.5 6.7 V17.3 L15.5 14
E718 Pin M10 2.5 L21.5 14 M11.5 4 L7 9 3.8 9.5 14.5 20.2 15 17 20 12.5 M9 15 L3 21
E71A Stop M5 5 H19 V19 H5 Z
E15B StopLegacy M5 5 H19 V19 H5 Z
E71C Filter M2.5 4.5 H21.5 L14 13 V19 L10 21 V13 Z
E71D AllFormats M3 3 H10 V10 H3 Z M14 3 H21 V10 H14 Z M3 14 H10 V21 H3 Z M14 14 H21 V21 H14 Z
E720 Microphone M9 6 A3 3 0 0 1 15 6 V12 A3 3 0 0 1 9 12 Z M5.5 11.5 V12 A6.5 6.5 0 0 0 18.5 12 V11.5 M12 18.5 V21.5 M8.7 21.5 H15.3
E721 Search M16.8 10 A6.8 6.8 0 1 1 3.2 10 A6.8 6.8 0 1 1 16.8 10 Z M15 15 L21 21
E722 Camera M3 7 H7 L8.8 4 H15.2 L17 7 H21 Q22 7 22 8 V19 Q22 20 21 20 H3 Q2 20 2 19 V8 Q2 7 3 7 Z M15.6 13.5 A3.6 3.6 0 1 1 8.4 13.5 A3.6 3.6 0 1 1 15.6 13.5 Z
E72B Back M21 12 H3 M10 5 L3 12 10 19
E72C Refresh M20 10 A8.2 8.2 0 1 0 19 17 M20.3 3.8 V10.3 H14
E737 Windows M3 7 H17 V21 H3 Z M7 3 H21 V17 M3.5 11 H16.5
E73E Check M3.5 12.5 L9.5 18.5 21 6
E740 Fullscreen M3.2 20.8 L20.7 3.3 M12.7 3.3 H20.7 V11.4 M3.2 12.7 V20.8 H11.3
E749 Print M6.5 8.5 V2.5 H17.5 V8.5 M6.5 17.5 H2.5 V8.5 H21.5 V17.5 H17.5 M6.5 14.5 H17.5 V21.5 H6.5 Z M17.5 11.5 H18
E74A Parent M12 21 V3 M4.8 10.2 L12 3 19.2 10.2
E74D Delete M3 6 H21 M8.5 6 V3 H15.5 V6 M5.5 6 L6.4 21 H17.6 L18.5 6 M10 9.5 V17.5 M14 9.5 V17.5
E74E Save M3 3 H17 L21 7 V21 H3 Z M7 3 V9 H16 V3 M7 21 V13 H17 V21
E753 Cloud M6 18.5 C0.5 18.5 0.3 10 6 9.5 C7 1.5 18.2 2.2 18.5 10 C23.8 10.7 23.2 18.5 18 18.5 Z
E767 Speaker M2.5 9 H6 L11 4.5 V19.5 L6 15 H2.5 Z M14.3 8.6 Q18.2 12 14.3 15.4 M17.5 5.3 Q25.3 12 17.5 18.7
E76B Previous M15.6 5 L8.4 12 15.6 19
E76C Next M8 5 L15.2 12 8 19
E77F Paste M8 5 H4.5 V21.5 H19.5 V5 H16 M8 3 H16 V7 H8 Z
E7AD Rotate M6.6 9 Q8 3 14 3 Q19 3 21 7.5 M21 2.5 V8 H15.5 M3.2 12.3 L10.3 8.3 16 17.8 8.9 21.8 Z
E7C3 File M5 2.5 H13.8 L19 7.8 V21.5 H5 Z M13.2 3.3 V8 H18.3
E7C8 Record M21 12 A9 9 0 1 1 3 12 A9 9 0 1 1 21 12 Z M15.5 12 A3.5 3.5 0 1 1 8.5 12 A3.5 3.5 0 1 1 15.5 12 Z
E7F4 Screen M2 4 H22 V17 H2 Z M12 17 V21 M7.5 21 H16.5
E7F5 AudioDevices M3.5 15 V11 A8.5 8.5 0 0 1 20.5 11 V15 M3.5 12.5 H7 V20 H4.5 Q3.5 20 3.5 19 Z M20.5 12.5 H17 V20 H19.5 Q20.5 20 20.5 19 Z
E80F Home M2.5 11 L12 2.8 21.5 11 M5 9.5 V21 H10 V15 H14 V21 H19 V9.5
E890 View M1.8 12 Q6.2 4.8 12 4.8 Q17.8 4.8 22.2 12 Q17.8 19.2 12 19.2 Q6.2 19.2 1.8 12 Z M15.1 12 A3.1 3.1 0 1 1 8.9 12 A3.1 3.1 0 1 1 15.1 12 Z
E895 Sync M3.5 10 Q5 4 11 4 H19.8 M16.4 1.9 L20 4.4 16.8 8 M20.5 14 Q19 20 13 20 H4.2 M7.6 22.1 L4 19.6 7.2 16
E896 Download M12 2.5 V15.5 M6.2 9.7 L12 15.5 17.8 9.7 M3.5 16 V21 H20.5 V16
E8A5 Document M5 2.5 H13.8 L19 7.8 V21.5 H5 Z M13.2 3.3 V8 H18.3 M8.3 12 H15.7 M8.3 15.5 H15.7
E8A7 NewWindow M9 3 H21 V15 M20 4 L10.8 13.2 M6 6 H3 V21 H18 V17.5
E8A9 Preview M3 3 H10 V10 H3 Z M14 3 H21 V10 H14 Z M3 14 H10 V21 H3 Z M14 14 H21 V21 H14 Z
E8AC Rename M5 6 H2 V18 H5 M10 4 H17 M13.5 4 V20 M10 20 H17 M20 6 H22 V18 H20
E8B7 Folder M2.5 5 H9.5 L12 8 H21.5 V19.5 H2.5 Z
E8B9 Image M2.5 4 H21.5 V20 H2.5 Z M3 17 L8.5 11.5 12 15 15.5 11.5 21.5 17.5 M16.8 8 A1.1 1.1 0 1 1 14.6 8 A1.1 1.1 0 1 1 16.8 8 Z
E8C6 Cut M8.6 17.9 A2.9 2.9 0 1 1 2.8 17.9 A2.9 2.9 0 1 1 8.6 17.9 Z M14.9 17.9 A2.9 2.9 0 1 0 20.7 17.9 A2.9 2.9 0 1 0 14.9 17.9 Z M7.5 15.6 L17.4 2.7 M16.6 15.6 L6.7 2.7
E8C8 Copy M8 7.5 H21 V21 H8 Z M4 16.5 H3 V3 H16.5 V4.5
E8D6 Music M8 17 V5.5 L20 3 V14.5 M8 9 L20 6.5 M8 17.5 C8 21.1 1.9 22.3 1.9 18.6 C1.9 15.8 8 14.8 8 17.5 Z M20 15 C20 18.5 13.9 19.7 13.9 16.1 C13.9 13.3 20 12.3 20 15 Z
E8DA ExtractTo M2 7 H8.5 L10.6 9.5 H14 M2 7 V20 H21 V14 M15 3 H21 V9 M20.5 3.5 L13.5 10.5
E8E5 Open M3 19 V5 H9 L12 8 H19 V10 M3 19 L6.7 11 H22 L18.5 19 Z
E8ED RepeatOne M3 10 V8 Q3 6 5 6 H19 M16 3 L19.4 6 16 9 M21 14 V16 Q21 18 19 18 H5 M8 15 L4.6 18 8 21 M10.3 11 L12.7 9.6 V14.7 M10.6 14.7 H14.5
E8EE RepeatList M3 12 V8 Q3 6 5 6 H19 M16 3 L19.4 6 16 9 M21 12 V16 Q21 18 19 18 H5 M8 15 L4.6 18 8 21
E8F4 NewFolder M2.5 5 H9.5 L12 8 H21.5 V19.5 H2.5 Z M12.5 10.5 V17 M9.3 13.8 H15.7
E946 Info M21 12 A9 9 0 1 1 3 12 A9 9 0 1 1 21 12 Z M12 11 V16.8 M12 7.3 V7.8
E950 Chip M6 6 H18 V18 H6 Z M9.5 9.5 H14.5 V14.5 H9.5 Z M9 2 V5.5 M15 2 V5.5 M9 18.5 V22 M15 18.5 V22 M2 9 H5.5 M2 15 H5.5 M18.5 9 H22 M18.5 15 H22
E9A6 Fit M3 8 V3 H8 M16 3 H21 V8 M21 16 V21 H16 M8 21 H3 V16 M8.5 7.5 H15.5 V16.5 H8.5 Z
EA37 List M3 5 H5 M8.5 5 H21 M3 12 H5 M8.5 12 H21 M3 19 H5 M8.5 19 H21
F012 ZipFolder M2.5 5 H9.5 L12 8 H21.5 V19.5 H2.5 Z M14 8 V10 H16 V12 H14 V14 H16 V16 H14
E190 Captions M2.5 4.5 H21.5 V19.5 H2.5 Z M5.5 11.5 H10 M13 11.5 H18.5 M5.5 15.5 H8 M11 15.5 H18.5
"@ -split '\r?\n'

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
