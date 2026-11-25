# PowerShell 7. -Root supports isolated regression fixtures without reading user workspace data.
[CmdletBinding()]
param([string]$Root = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'
$Root = [IO.Path]::GetFullPath($Root)
$limits = [ordered]@{ 'AGENTS.md'=2000; 'CLAUDE.md'=6000; 'docs/HANDOVER.md'=4000; 'docs/REQUIREMENTS.md'=50000; 'docs/DEVICE-CHECKS.md'=20000 }
$active = @($limits.Keys) + @('docs/README.md', 'docs/development/maintenance-notes.md', 'docs/development/official-release-and-install.md')
$errorsFound = [Collections.Generic.List[string]]::new()
$documents = @{}; $anchors = @{}
function Fail([string]$rule, [string]$message) { $errorsFound.Add("[$rule] $message") }
function Is-PrivatePath([string]$path) { return $path.Replace('\', '/') -match '(?i)(^|/)\.codex(/|$)' }
function Read-Document([string]$file) {
    if (Is-PrivatePath $file) { throw 'Private workspace data is not a documentation input.' }
    if (!$documents.ContainsKey($file)) {
        $path = Join-Path $Root $file
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) { Fail 'required-file' $file; return '' }
        $documents[$file] = [IO.File]::ReadAllText($path).Replace("`r`n", "`n")
    }
    return $documents[$file]
}
function Get-Anchors([string]$file) {
    if (!$anchors.ContainsKey($file)) {
        $set = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $body = Read-Document $file
        foreach ($m in [regex]::Matches($body, '(?im)<a\s+[^>]*(?:id|name)=["'']([^"'']+)["'']')) {
            if (!$set.Add($m.Groups[1].Value)) { Fail 'duplicate-anchor' "$file#$($m.Groups[1].Value)" }
        }
        $headings = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($m in [regex]::Matches($body, '(?m)^#{1,6}[ \t]+(.+)$')) {
            $title = [regex]::Replace($m.Groups[1].Value.Trim().ToLowerInvariant(), '\[([^\]]+)\]\([^)]+\)', '$1')
            $slug = [regex]::Replace($title, '[^\p{L}\p{M}\p{N}_ -]', '').Replace(' ', '-')
            $id = $slug; $suffix = 0
            while (!$headings.Add($id)) { $suffix++; $id = "$slug-$suffix" }
            [void]$set.Add($id)
        }
        $anchors[$file] = $set
    }
    return ,$anchors[$file]
}
function Get-Section([string]$body, [string]$heading) {
    $m = [regex]::Match($body, '(?ms)^## ' + [regex]::Escape($heading) + '\s*\n(.*?)(?=^## |\z)')
    if (!$m.Success) { Fail 'required-section' $heading; return '' }
    return $m.Groups[1].Value
}
function Get-RequirementRows([string]$body, [string]$file) {
    $rows = @{}
    foreach ($line in ($body -split "`n")) {
        if ($line -notmatch '^\| <a id="a(\d+)"></a>A(\d+) \| ([^|]+) \| (.+) \|$') {
            if ($line -match '^\|.*<a id="a\d+"') { Fail 'requirement-row' "$file malformed registration" }
            continue
        }
        $id = [int]$Matches[1]; $labelId = [int]$Matches[2]; $status = $Matches[3].Trim()
        if ($id -ne $labelId -or $rows.ContainsKey($id)) { Fail 'requirement-id' "$file duplicate or mismatched A$id" }
        $rows[$id] = @{ Status=$status; Line=$line }
    }
    return $rows
}
$counts = foreach ($file in $limits.Keys) {
    $body = Read-Document $file
    $chars = $body.Length
    for ($i=0; $i -lt $body.Length-1; $i++) {
        if ([char]::IsSurrogatePair($body, $i)) { $chars--; $i++ }
    }
    if ($chars -gt $limits[$file]) { Fail 'size' "$file $chars > $($limits[$file])" }
    "$file $chars/$($limits[$file])"
}
foreach ($heading in @('문서 운영과 검증','작업 선택과 책임')) { [void](Get-Section (Read-Document 'CLAUDE.md') $heading) }
$handover = Read-Document 'docs/HANDOVER.md'
[void](Get-Section $handover '현재 상태')
$nextSection = Get-Section $handover '열린 작업과 다음 시작점'
if ([regex]::Matches($handover, '(?m)^#{2,6} ').Count -ne 2 -or $handover -match '(?m)^\|') { Fail 'handover-role' 'Only current state and next actions; no history tables/subsections.' }
$items = [regex]::Matches($handover, '(?ms)^- .*?(?=^- |^#|\n\s*\n|\z)')
if ($items.Count -gt 10) { Fail 'handover-role' 'More than ten current-state/next-action items.' }
foreach ($item in $items) { if ($item.Value.Trim().Length -gt 650) { Fail 'handover-role' 'An item exceeds 650 characters; link its canonical detail.' } }
# Mentioning current/backup versions is valid; release-per-bullet chronology is not.
if ([regex]::Matches($handover, '(?m)^-\s+(?:A\d+\s+)?v\d+\.\d+\.\d+\b').Count -gt 1 -or $handover -match '(?im)^#{1,6} .*?(?:연대기|릴리스 이력|배포 이력|세션 기록)') { Fail 'handover-history' 'Use existing result/evidence entries for past releases.' }
$req = Read-Document 'docs/REQUIREMENTS.md'
$openSection = Get-Section $req '열린 작업과 보류'
$openRows = Get-RequirementRows $openSection 'REQUIREMENTS'
$indexFile = 'docs/history/requirements-completed-index.md'
$indexRows = Get-RequirementRows (Read-Document $indexFile) $indexFile
$lastMatch = [regex]::Match($req, '마지막 등록 번호는 \*\*A(\d+)\*\*')
if (!$lastMatch.Success) { Fail 'requirement-id' 'Last registered ID is missing.' }
else {
    $last = [int]$lastMatch.Groups[1].Value
    if ($last -lt 1 -or $indexRows.Count -ne $last) { Fail 'requirement-id' 'Index count differs from last registered ID.' }
    for ($id=1; $id -le $last; $id++) { if (!$indexRows.ContainsKey($id)) { Fail 'requirement-id' "Missing A$id from index." } }
    foreach ($id in $indexRows.Keys) { if ($id -gt $last -or $id -lt 1) { Fail 'requirement-id' "Out-of-range A$id" } }
}
$openStatuses = @('진행','검토 대기','미착수','부분','보류','조사','백로그')
foreach ($id in $openRows.Keys) {
    if ($openRows[$id].Status -notin $openStatuses) { Fail 'open-status' "A$id is not open: $($openRows[$id].Status)" }
    if (!$indexRows.ContainsKey($id) -or $indexRows[$id].Status -ne '열림' -or !$indexRows[$id].Line.Contains("(../REQUIREMENTS.md#a$id)")) { Fail 'status-reference' "A$id needs one index pointer to its open state." }
}
foreach ($id in $indexRows.Keys) {
    if ($indexRows[$id].Status -eq '열림' -and !$openRows.ContainsKey($id)) { Fail 'status-reference' "A$id marked open without current row." }
    if ($indexRows[$id].Line.Length -gt 900) { Fail 'row-detail' "Index A$id exceeds 900 characters." }
}
foreach ($m in [regex]::Matches($nextSection, '(?m)^-\s+\*\*A(\d+)\b')) {
    if (!$openRows.ContainsKey([int]$m.Groups[1].Value)) { Fail 'handover-status' "Selected A$($m.Groups[1].Value) is no longer open." }
}
foreach ($file in @('docs/REQUIREMENTS.md','docs/DEVICE-CHECKS.md')) {
    $body = if ($file -eq 'docs/REQUIREMENTS.md') { $openSection } else { Read-Document $file }
    foreach ($line in ($body -split "`n")) { if ($line.StartsWith('|') -and $line.Length -gt 900) { Fail 'row-detail' "$file has a row longer than 900 characters." } }
}
foreach ($m in [regex]::Matches($req, '(?m)^<a id="a(\d+)"></a>\[A(\d+)\]\(history/requirements-completed-index\.md#a(\d+)\)$')) {
    $id = [int]$m.Groups[1].Value
    if ($m.Groups[2].Value -ne "$id" -or $m.Groups[3].Value -ne "$id" -or !$indexRows.ContainsKey($id) -or $indexRows[$id].Status -eq '열림') { Fail 'status-reference' "Invalid completed alias A$id" }
}
# Exclude private data before traversal. Fixtures do not need a Git repository.
$hasGit = Test-Path -LiteralPath (Join-Path $Root '.git')
if ($hasGit) {
    $files = @(git -C $Root -c core.quotepath=false ls-files --cached -- '*.md' ':(exclude).codex/**' ':(exclude)**/.codex/**')
    if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate tracked documentation.' }
    $files += @(git -C $Root -c core.quotepath=false ls-files --others --exclude-standard -- 'docs/*.md' 'docs/**/*.md' 'AGENTS.md' 'CLAUDE.md' ':(exclude)**/.codex/**')
    if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate new documentation.' }
} else {
    $files = @($active + @($indexFile))
    $pending = [Collections.Generic.Stack[string]]::new()
    if (Test-Path -LiteralPath (Join-Path $Root 'docs')) { $pending.Push((Join-Path $Root 'docs')) }
    while ($pending.Count) {
        foreach ($entry in Get-ChildItem -LiteralPath $pending.Pop()) {
            if (Is-PrivatePath $entry.FullName) { continue }
            if ($entry.PSIsContainer) { $pending.Push($entry.FullName) }
            elseif ($entry.Extension -eq '.md') { $files += [IO.Path]::GetRelativePath($Root,$entry.FullName) }
        }
    }
}
$files = @($files | ForEach-Object { $_.Replace('\','/') } | Where-Object { !(Is-PrivatePath $_) } | Sort-Object -Unique)
$shallow = $hasGit -and ((git -C $Root rev-parse --is-shallow-repository) -eq 'true')
$historicalFiles = @{}; $unavailableCommits = [Collections.Generic.HashSet[string]]::new(); $fixedLinks = 0
function Test-FixedGitLink([string]$file,[string]$href) {
    if ($href -notmatch '^https://github\.com/zpstudios/kotu/blob/([0-9a-f]{40})/([^#]+)(?:#L(\d+)(?:-L(\d+))?)?$') { return }
    $commit=$Matches[1]; $path=[Uri]::UnescapeDataString($Matches[2])
    $hasRange=[bool]$Matches[3]
    $first=if ($hasRange) { [int]$Matches[3] } else { 0 }
    $lastLine=if ($Matches[4]) { [int]$Matches[4] } else { $first }
    if (Is-PrivatePath $path) { Fail 'private-path' "$file references private data"; return }
    if (!$hasGit) { return }
    $key="${commit}:$path"
    if (!$historicalFiles.ContainsKey($key)) {
        if ($unavailableCommits.Contains($commit)) { return }
        git -C $Root cat-file -e "${commit}^{commit}" 2>$null
        if ($LASTEXITCODE -ne 0) {
            if ($shallow) { [void]$unavailableCommits.Add($commit); return }
            Fail 'fixed-git-link' "$file missing commit: $commit"; return
        }
        $lines=@(git -C $Root show $key 2>$null)
        if ($LASTEXITCODE -ne 0) { Fail 'fixed-git-link' "$file missing historical path: $key"; return }
        $historicalFiles[$key]=$lines.Count
    }
    if ($hasRange -and ($first -lt 1 -or $lastLine -lt $first -or $lastLine -gt $historicalFiles[$key])) { Fail 'fixed-git-link' "$file missing historical line range: $href" }
    $script:fixedLinks++
}
$linkCount=0; $chunks=@{}
foreach ($file in $files) {
    if (!(Test-Path -LiteralPath (Join-Path $Root $file) -PathType Leaf)) { continue }
    $body=Read-Document $file
    [void](Get-Anchors $file)
    $prose=[regex]::Replace($body,'(?ms)^[ \t]*(\x60{3,}|~{3,})[^\n]*\n.*?^[ \t]*\1[ \t]*$','')
    foreach ($m in [regex]::Matches($prose,'\]\((?:<([^>]+)>|([^\s)]+))(?:\s+"[^"]*")?\)')) {
        $href=$m.Groups[1].Value+$m.Groups[2].Value
        if ($href -match '^(?:[a-z][a-z\d+.-]*:|//)') { Test-FixedGitLink $file $href; continue }
        $parts=$href -split '#',2; $relative=[Uri]::UnescapeDataString($parts[0]); $filePath=Join-Path $Root $file
        $dest=if (!$relative) { $filePath } elseif ($relative.StartsWith('/')) { Join-Path $Root $relative.TrimStart('/') } else { Join-Path (Split-Path $filePath) $relative }
        $target=[IO.Path]::GetRelativePath($Root,[IO.Path]::GetFullPath($dest)).Replace('\','/')
        $linkCount++
        if (Is-PrivatePath $target) { Fail 'private-path' "$file -> $href"; continue }
        if ($target -eq '..' -or $target.StartsWith('../') -or !(Test-Path -LiteralPath $dest)) { Fail 'local-link' "$file -> $href" }
        elseif ($parts.Count -eq 2 -and $parts[1] -and $target.EndsWith('.md') -and !(Get-Anchors $target).Contains([Uri]::UnescapeDataString($parts[1]))) { Fail 'local-link' "$file -> $href (anchor)" }
    }
    if ($file -in $active) {
        # A changed neighbor must not hide a repeated table/list row. Same-file duplicates also fail.
        foreach ($chunk in [regex]::Split($prose,'\n\s*\n|(?m)(?=^\|)|(?m)(?=^- )')) {
            $key=[regex]::Replace($chunk.Trim(),'\s+',' ')
            if ($key.Length -lt 150) { continue }
            if ($chunks.ContainsKey($key)) { Fail 'duplicate-text' "$file repeats $($chunks[$key]): $($key.Substring(0,70))..." } else { $chunks[$key]=$file }
        }
        foreach ($m in [regex]::Matches($prose,'\x60(docs/[\w./-]+\.md)\x60')) {
            if (Is-PrivatePath $m.Groups[1].Value) { Fail 'private-path' $file; continue }
            if (!(Test-Path -LiteralPath (Join-Path $Root $m.Groups[1].Value))) { Fail 'local-link' "$file stale path $($m.Groups[1].Value)" }
        }
    }
}
$counts
"Internal links checked: $linkCount. Fixed Git links checked: $fixedLinks."
if (!$hasGit -or $unavailableCommits.Count) { "Historical Git validation unavailable: fixture/no Git or $($unavailableCommits.Count) commits outside shallow history." }
if ($errorsFound.Count) {
    $errorsFound | Sort-Object -Unique | ForEach-Object { Write-Output "ERROR $_" }
    throw "Documentation checks failed ($($errorsFound.Count))."
}
'Documentation checks passed. Meaning, preservation destinations and observation limits require review.'
