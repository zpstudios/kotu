# Run from any working directory with PowerShell 7.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$limits = [ordered]@{
    'AGENTS.md' = 2000
    'CLAUDE.md' = 6000
    'docs/HANDOVER.md' = 20000
    'docs/REQUIREMENTS.md' = 50000
    'docs/DEVICE-CHECKS.md' = 20000
}
$required = @{
    'CLAUDE.md' = @('문서 운영과 검증', '작업 선택과 책임')
    'docs/HANDOVER.md' = @('현재 상태', '열린 작업과 다음 시작점')
}
$errorsFound = [Collections.Generic.List[string]]::new()
$documents = @{}
$anchors = @{}
function Read-Document([string]$file) {
    if (!$documents.ContainsKey($file)) {
        $documents[$file] = [IO.File]::ReadAllText((Join-Path $root $file)).Replace("`r`n", "`n")
    }
    return $documents[$file]
}
function Get-Anchors([string]$file) {
    if (!$anchors.ContainsKey($file)) {
        $set = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $body = Read-Document $file
        foreach ($match in [regex]::Matches($body, '(?im)<a\s+[^>]*(?:id|name)=["'']([^"'']+)["'']')) {
            [void]$set.Add($match.Groups[1].Value)
        }
        $headings = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($match in [regex]::Matches($body, '(?m)^#{1,6}[ \t]+(.+)$')) {
            $title = $match.Groups[1].Value.Trim().ToLowerInvariant()
            $title = [regex]::Replace($title, '\[([^\]]+)\]\([^)]+\)', '$1')
            $slug = [regex]::Replace($title, '[^\p{L}\p{M}\p{N}_ -]', '').Replace(' ', '-')
            $id = $slug
            $suffix = 0
            while (!$headings.Add($id)) { $suffix++; $id = "$slug-$suffix" }
            [void]$set.Add($id)
        }
        $anchors[$file] = $set
    }
    return ,$anchors[$file]
}
$counts = foreach ($file in $limits.Keys) {
    $body = Read-Document $file
    $chars = $body.Length - [regex]::Matches($body, '[\uD800-\uDBFF][\uDC00-\uDFFF]').Count
    if ($chars -gt $limits[$file]) { $errorsFound.Add("$file exceeds $($limits[$file]) characters") }
    foreach ($heading in $required[$file]) {
        if (!$body.Contains("## $heading")) { $errorsFound.Add("$file missing section $heading") }
    }
    "$file $chars/$($limits[$file])"
}
$files = @(git -C $root -c core.quotepath=false ls-files --cached --others --exclude-standard -- '*.md' | Sort-Object -Unique)
if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate repository documents.' }
$linkCount = 0
$paragraphs = @{}
$duplicates = 0
foreach ($file in $files) {
    if (!(Test-Path -LiteralPath (Join-Path $root $file) -PathType Leaf)) { continue }
    $body = Read-Document $file
    $prose = [regex]::Replace($body, '(?ms)^[ \t]*(\x60{3,}|~{3,})[^\n]*\n.*?^[ \t]*\1[ \t]*$', '')
    foreach ($match in [regex]::Matches($prose, '\]\((?:<([^>]+)>|([^\s)]+))(?:\s+"[^"]*")?\)')) {
        $href = $match.Groups[1].Value + $match.Groups[2].Value
        if ($href -match '^(?:[a-z][a-z\d+.-]*:|//)') { continue }
        $parts = $href -split '#', 2
        $relative = [Uri]::UnescapeDataString($parts[0])
        $filePath = Join-Path $root $file
        $dest = if (!$relative) { $filePath } elseif ($relative.StartsWith('/')) {
            Join-Path $root $relative.TrimStart('/')
        } else { Join-Path (Split-Path $filePath) $relative }
        $target = [IO.Path]::GetRelativePath($root, [IO.Path]::GetFullPath($dest)).Replace('\', '/')
        $linkCount++
        if ($target -eq '..' -or $target.StartsWith('../') -or !(Test-Path -LiteralPath $dest)) {
            $errorsFound.Add("$file -> $href")
        } elseif ($parts.Count -eq 2 -and $parts[1] -and $target.EndsWith('.md') -and
            !(Get-Anchors $target).Contains([Uri]::UnescapeDataString($parts[1]))) {
            $errorsFound.Add("$file -> $href (anchor)")
        }
    }
    if ($limits.Contains($file)) {
        foreach ($chunk in ($prose -split '\n\s*\n')) {
            $key = [regex]::Replace($chunk.Trim(), '\s+', ' ')
            if ($key.Length -lt 150) { continue }
            if ($paragraphs.ContainsKey($key) -and $paragraphs[$key] -ne $file) { $duplicates++ }
            $paragraphs[$key] = $file
        }
        foreach ($m in [regex]::Matches($prose, '\x60(docs/[\w./-]+\.md)\x60')) {
            if (!(Test-Path -LiteralPath (Join-Path $root $m.Groups[1].Value))) { $errorsFound.Add("$file stale path $($m.Groups[1].Value)") }
        }
    }
}
$counts
"Internal links checked: $linkCount. Repeated paragraphs across active documents (review candidates): $duplicates."
if ($errorsFound.Count) {
    $errorsFound | Sort-Object -Unique | ForEach-Object { Write-Output "ERROR $_" }
    throw "Documentation checks failed ($($errorsFound.Count))."
}
'Documentation checks passed.'
