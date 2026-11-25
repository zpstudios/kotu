# PowerShell 7. -Root supports isolated regression fixtures without reading user workspace data.
[CmdletBinding()]
param([string]$Root = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'
$Root = [IO.Path]::GetFullPath($Root)
$limits = [ordered]@{ 'AGENTS.md'=2000; 'CLAUDE.md'=6000; 'docs/HANDOVER.md'=4000; 'docs/REQUIREMENTS.md'=12000; 'docs/DEVICE-CHECKS.md'=20000; 'docs/features/product-contracts.md'=12000 }
$active = @($limits.Keys) + @('docs/README.md', 'docs/development/maintenance-notes.md', 'docs/development/official-release-and-install.md')
$errorsFound = [Collections.Generic.List[string]]::new()
$documents = @{}; $anchors = @{}
function Fail([string]$rule, [string]$message) { $errorsFound.Add("[$rule] $message") }
function Is-PrivatePath([string]$path) { return $path.Replace('\', '/') -match '(?i)(^|/)\.codex(/|$)' }
function Normalize-TableIndent([string]$body) {
    # Leading Markdown indentation must neither hide foreign tables nor invalidate current rows.
    return [regex]::Replace($body,'(?m)^[ \t]+(?=\|)','')
}
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
        $rows[$id] = @{ Status=$status; Line=$line; Cells=@($line.Split('|') | ForEach-Object { $_.Trim() }) }
    }
    return $rows
}
function Get-CandidateRows([string]$body, [string]$file) {
    $rows = @{}
    foreach ($line in ($body -split "`n")) {
        if ($line -notmatch '^\| <a id="candidate-(\d+)"></a>(\d+) \| ([^|]+) \| (.+) \|$') {
            if ($line -match '^\| (?:<a id="candidate-|\d+ \|)') { Fail 'candidate-row' "$file malformed candidate" }
            continue
        }
        $id=[int]$Matches[1]; $label=[int]$Matches[2]; $status=$Matches[3].Trim()
        if ($id -ne $label -or $rows.ContainsKey($id)) { Fail 'candidate-id' "$file duplicate or mismatched candidate $id" }
        $rows[$id]=@{ Status=$status; Line=$line; Cells=@($line.Split('|') | ForEach-Object { $_.Trim() }) }
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
$handover = Normalize-TableIndent (Read-Document 'docs/HANDOVER.md')
[void](Get-Section $handover '현재 상태')
$nextSection = Get-Section $handover '열린 작업과 다음 시작점'
if ([regex]::Matches($handover, '(?m)^#{2,6} ').Count -ne 2 -or $handover -match '(?m)^\|') { Fail 'handover-role' 'Only current state and next actions; no history tables/subsections.' }
$items = [regex]::Matches($handover, '(?ms)^- .*?(?=^- |^#|\n\s*\n|\z)')
if ($items.Count -gt 10) { Fail 'handover-role' 'More than ten current-state/next-action items.' }
foreach ($item in $items) { if ($item.Value.Trim().Length -gt 650) { Fail 'handover-role' 'An item exceeds 650 characters; link its canonical detail.' } }
# Mentioning current/backup versions is valid; release-per-bullet chronology is not.
if ([regex]::Matches($handover, '(?m)^-\s+(?:A\d+\s+)?v\d+\.\d+\.\d+\b').Count -gt 1 -or $handover -match '(?im)^#{1,6} .*?(?:연대기|릴리스 이력|배포 이력|세션 기록)') { Fail 'handover-history' 'Use existing result/evidence entries for past releases.' }
$req = Normalize-TableIndent (Read-Document 'docs/REQUIREMENTS.md')
$openSection = Get-Section $req '열린 작업과 보류'
$candidateSection = Get-Section $req '미해결 후보'
$compatSection = Get-Section $req '이전 주소 호환'
$intro = ($req -split '(?m)^## ',2)[0]
if ($intro -match '(?m)^\|') { Fail 'requirements-role' 'The introduction routes documents; tables belong to the two remaining-work sections.' }
$allowedSections = @('열린 작업과 보류','미해결 후보','이전 주소 호환')
foreach ($m in [regex]::Matches($req,'(?m)^#{2,6} (.+)$')) {
    if (!$m.Value.StartsWith('## ') -or $m.Groups[1].Value.Trim() -notin $allowedSections) { Fail 'requirements-role' 'Only remaining work, unresolved candidates and legacy redirects belong here.' }
}
if ([regex]::Matches($req,'(?m)^## ').Count -ne $allowedSections.Count) { Fail 'requirements-role' 'Duplicate or missing section.' }
$openHeader='| ID | 상태 | 남은 일 유형 | 남은 일·착수 조건 | 근거 |'
$candidateHeader='| 후보 | 상태 | 남은 일 유형 | 남은 일·판단 조건 |'
foreach ($section in @(@{ Body=$openSection; Header=$openHeader; Prefix='a\d+' },@{ Body=$candidateSection; Header=$candidateHeader; Prefix='candidate-\d+' })) {
    if (!$section.Body.Contains($section.Header)) { Fail 'requirements-role' 'Use the remaining-work table schema.' }
    foreach ($line in ($section.Body -split "`n")) {
        if ($line.StartsWith('|') -and $line -ne $section.Header -and $line -notmatch '^\|[-| ]+\|$' -and $line -notmatch ('^\| <a id="'+$section.Prefix+'"></a>')) {
            Fail 'requirements-role' 'Results or contract tables cannot be added to remaining work.'
        }
    }
}
if ($compatSection -match '(?m)^\|') { Fail 'requirements-role' 'Legacy addresses contain redirects, not decision/result tables.' }
$openRows = Get-RequirementRows $openSection 'REQUIREMENTS'
$indexFile = 'docs/history/requirements-completed-index.md'
$indexBody = Normalize-TableIndent (Read-Document $indexFile)
$indexRows = Get-RequirementRows $indexBody $indexFile
$openCandidates = Get-CandidateRows $candidateSection 'REQUIREMENTS'
$candidateIndex = Get-CandidateRows $indexBody $indexFile
$lastMatch = [regex]::Match($req, '마지막 등록 번호는 \*\*A(\d+)\*\*')
if (!$lastMatch.Success) { Fail 'requirement-id' 'Last registered ID is missing.' }
else {
    $last = [int]$lastMatch.Groups[1].Value
    if ($last -lt 1 -or $indexRows.Count -ne $last) { Fail 'requirement-id' 'Index count differs from last registered ID.' }
    for ($id=1; $id -le $last; $id++) { if (!$indexRows.ContainsKey($id)) { Fail 'requirement-id' "Missing A$id from index." } }
    foreach ($id in $indexRows.Keys) { if ($id -gt $last -or $id -lt 1) { Fail 'requirement-id' "Out-of-range A$id" } }
}
$openStatuses = @('진행','검토 대기','미착수','부분','보류','조사','백로그')
$workKinds = @('개발','조사','결정')
foreach ($id in $openRows.Keys) {
    if ($openRows[$id].Status -notin $openStatuses) { Fail 'open-status' "A$id is not open: $($openRows[$id].Status)" }
    if ($openRows[$id].Cells.Count -ne 7 -or $openRows[$id].Cells[3] -notin $workKinds -or !$openRows[$id].Cells[4]) { Fail 'remaining-work' "A$id needs a remaining development/investigation/decision action." }
    if (!$indexRows.ContainsKey($id) -or $indexRows[$id].Status -ne '열림' -or !$indexRows[$id].Line.Contains("(../REQUIREMENTS.md#a$id)")) { Fail 'status-reference' "A$id needs one index pointer to its open state." }
}
foreach ($id in $indexRows.Keys) {
    if ($indexRows[$id].Status -eq '열림' -and !$openRows.ContainsKey($id)) { Fail 'status-reference' "A$id marked open without current row." }
    if ($indexRows[$id].Status -eq '검수 대기') {
        if ($openRows.ContainsKey($id) -or !$indexRows[$id].Line.Contains("(../DEVICE-CHECKS.md#a$id)") -or !(Get-Anchors 'docs/DEVICE-CHECKS.md').Contains("a$id")) { Fail 'verification-reference' "A$id verification must live only in DEVICE-CHECKS with its own anchor." }
    }
    if ($indexRows[$id].Line.Length -gt 900) { Fail 'row-detail' "Index A$id exceeds 900 characters." }
}
$lastCandidateMatch = [regex]::Match($req,'마지막 후보 번호는 \*\*(\d+)\*\*')
if (!$lastCandidateMatch.Success) { Fail 'candidate-id' 'Last candidate number is missing.' }
else {
    $lastCandidate=[int]$lastCandidateMatch.Groups[1].Value
    if ($candidateIndex.Count -ne $lastCandidate) { Fail 'candidate-id' 'Candidate registry count differs from last ID.' }
    for ($id=1; $id -le $lastCandidate; $id++) { if (!$candidateIndex.ContainsKey($id)) { Fail 'candidate-id' "Missing candidate $id" } }
    foreach ($id in $candidateIndex.Keys) { if ($id -lt 1 -or $id -gt $lastCandidate) { Fail 'candidate-id' "Out-of-range candidate $id" } }
}
$candidateStatuses = $openStatuses + @('재확인','통합 후보','결정 대기','후보','대조 후 판단','조건부 보류','UX 후보','성능 후보','재현 필요','주석 감사 후보','실측 필요','코드 감사·미확인','대형 설계 후보')
foreach ($id in $openCandidates.Keys) {
    $row=$openCandidates[$id]
    if ($row.Status -notin $candidateStatuses) { Fail 'candidate-status' "Candidate $id has a terminal state in remaining work." }
    if ($row.Cells.Count -ne 6 -or $row.Cells[3] -notin $workKinds -or !$row.Cells[4]) { Fail 'remaining-work' "Candidate $id needs an unresolved action." }
    if (!$candidateIndex.ContainsKey($id) -or $candidateIndex[$id].Status -ne '열림' -or !$candidateIndex[$id].Line.Contains("(../REQUIREMENTS.md#candidate-$id)")) { Fail 'candidate-reference' "Candidate $id needs its permanent index pointer." }
}
foreach ($id in $candidateIndex.Keys) {
    if ($candidateIndex[$id].Status -eq '열림' -and !$openCandidates.ContainsKey($id)) { Fail 'candidate-reference' "Candidate $id marked open without remaining work." }
    if ($candidateIndex[$id].Line.Length -gt 900) { Fail 'row-detail' "Candidate result $id exceeds 900 characters." }
}
# A leading requirement ID selects an action, with or without Markdown emphasis.
# IDs mentioned later in a context/remaining-scope item are only references.
foreach ($m in [regex]::Matches($nextSection, '(?m)^-[ \t]+(?:\*\*|__)?A(\d+)\b')) {
    $id=[int]$m.Groups[1].Value
    if (!$openRows.ContainsKey($id) -and (!$indexRows.ContainsKey($id) -or $indexRows[$id].Status -ne '검수 대기')) { Fail 'handover-status' "Selected A$id has neither remaining work nor pending verification." }
}
foreach ($file in @('docs/REQUIREMENTS.md','docs/DEVICE-CHECKS.md')) {
    $body = if ($file -eq 'docs/REQUIREMENTS.md') { $openSection + $candidateSection } else { Normalize-TableIndent (Read-Document $file) }
    foreach ($line in ($body -split "`n")) { if ($line.StartsWith('|') -and $line.Length -gt 900) { Fail 'row-detail' "$file has a row longer than 900 characters." } }
}
# Only addresses already issued at the 8d93960 partition baseline get legacy redirects.
# All new public A/candidate links use the permanent index, so completion cannot grow this list.
$legacyAIds = @(19,45,112,115,142,281,363,380,398,399,401,406,407,418,419,420,421,422,423,424,425,426,427,428,429,430,431,432,433,434,435,436)
foreach ($m in [regex]::Matches($compatSection, '(?m)^<a id="a(\d+)"></a>\[A(\d+)\]\(history/requirements-completed-index\.md#a(\d+)\)$')) {
    $id = [int]$m.Groups[1].Value
    if ($id -notin $legacyAIds) { Fail 'legacy-growth' "A$id must use its permanent index address, not a new legacy redirect." }
    if ($m.Groups[2].Value -ne "$id" -or $m.Groups[3].Value -ne "$id" -or !$indexRows.ContainsKey($id) -or $indexRows[$id].Status -eq '열림') { Fail 'status-reference' "Invalid completed alias A$id" }
}
foreach ($line in ($compatSection -split "`n")) {
    if ($line -match '<a id="a\d+"' -and $line -notmatch '^<a id="a(\d+)"></a>\[A\1\]\(history/requirements-completed-index\.md#a\1\)$') { Fail 'legacy-growth' 'Use one compact legacy redirect without detail.' }
}
foreach ($m in [regex]::Matches($req,'(?m)^.*<a id="a(\d+)"></a>.*$')) {
    $id=[int]$m.Groups[1].Value
    if ($openRows.ContainsKey($id)) {
        if ($m.Value -ne $openRows[$id].Line) { Fail 'legacy-growth' "Active A$id must have only its remaining-work row." }
    } elseif (!$compatSection.Contains($m.Value) -or $id -notin $legacyAIds) { Fail 'legacy-growth' "A$id is not a grandfathered redirect in the compatibility section." }
}
foreach ($m in [regex]::Matches($req,'(?m)^.*<a id="candidate-(\d+)"></a>.*$')) {
    $id=[int]$m.Groups[1].Value
    if (!$openCandidates.ContainsKey($id) -or $m.Value -ne $openCandidates[$id].Line) { Fail 'candidate-reference' "Candidate $id current anchors belong only to unresolved rows." }
}
if ($lastMatch.Success -and [int]$lastMatch.Groups[1].Value -ge 436) {
    foreach ($id in $legacyAIds) { if (!(Get-Anchors 'docs/REQUIREMENTS.md').Contains("a$id")) { Fail 'legacy-link' "Lost pre-partition address A$id" } }
}
if ($compatSection -notmatch '(?m)^<a id="current-decisions"></a>\[.+\]\(features/product-contracts\.md\)$') { Fail 'requirements-role' 'The old decision address redirects to product contracts.' }
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
    $prose=Normalize-TableIndent ([regex]::Replace($body,'(?ms)^[ \t]*(\x60{3,}|~{3,})[^\n]*\n.*?^[ \t]*\1[ \t]*$',''))
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
$global:LASTEXITCODE = 0
'Documentation checks passed. Meaning, preservation destinations and observation limits require review.'
