# Behavior tests for the document lifecycle gate; no product build or user files are involved.
[CmdletBinding()]
param([switch]$SkipHostExitTests)
$ErrorActionPreference = 'Stop'
$checker = Join-Path $PSScriptRoot 'Test-Documentation.ps1'
$fixtureParent = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts')) + [IO.Path]::DirectorySeparatorChar
$fixture = Join-Path $fixtureParent ('kotu-doc-tests-' + [Guid]::NewGuid().ToString('N'))
$baseline = @{
    'AGENTS.md' = '# Project entry'
    'CLAUDE.md' = "# Rules`n`n## 문서 운영과 검증`n`nOne canonical document.`n`n## 작업 선택과 책임`n`nKeep permanent IDs."
    'docs/HANDOVER.md' = "# Current state`n`n## 현재 상태`n`n- Current official product and protected backup.`n`n## 열린 작업과 다음 시작점`n`n- **A2 부분**: [Remaining scope](REQUIREMENTS.md#a2)."
    'docs/REQUIREMENTS.md' = @'
# Requirements

마지막 등록 번호는 **A2**이다.

## 열린 작업과 보류

| ID | 상태 | 범위 | 다음 |
|---|---|---|---|
| <a id="a2"></a>A2 | 부분 | Implemented scope is complete; real input remains unobserved. | Observe only when selected. |

## 이전 완료 링크 호환

<a id="a1"></a>[A1](history/requirements-completed-index.md#a1)
'@
    'docs/DEVICE-CHECKS.md' = "# Verification`n`n[Open](REQUIREMENTS.md#a2)`n`n| Scope | Observed | Remaining |`n|---|---|---|`n| Input | Automatic input only | Physical input remains |"
    'docs/README.md' = '# Map'
    'docs/development/maintenance-notes.md' = '# Lifecycle procedure'
    'docs/development/official-release-and-install.md' = '# Release procedure'
    'docs/history/requirements-completed-index.md' = @'
# Requirement index

| ID | 결과 | 주제 | 근거 |
|---|---|---|---|
| <a id="a1"></a>A1 | 완료 | Initial scope | Existing source |
| <a id="a2"></a>A2 | 열림 | [Current scope](../REQUIREMENTS.md#a2) | Existing source |
'@
}
function Write-Fixture([string]$file, [string]$body) {
    $path = Join-Path $fixture $file
    [void][IO.Directory]::CreateDirectory((Split-Path $path))
    [IO.File]::WriteAllText($path, $body + "`n", [Text.UTF8Encoding]::new($false))
}
$passed = 0
function Assert-Case([string]$name, [scriptblock]$change, [string]$expectedRule = '') {
    foreach ($file in $baseline.Keys) { Write-Fixture $file $baseline[$file] }
    & $change
    $output = [Collections.Generic.List[string]]::new()
    $failed = $false
    try { & $checker -Root $fixture | ForEach-Object { $output.Add([string]$_) } }
    catch { $failed = $true; $output.Add($_.Exception.Message) }
    if ($expectedRule) {
        if (!$failed -or !($output -join "`n").Contains("[$expectedRule]")) { throw "$name did not fail for [$expectedRule]: $($output -join ' / ')" }
    } elseif ($failed) { throw "$name unexpectedly failed: $($output -join ' / ')" }
    $script:passed++
}
function Assert-HostExit([string]$name, [string]$invocation, [bool]$success, [string]$expectedText) {
    # Match the GitHub pwsh host: a dot-sourced script followed by LASTEXITCODE propagation.
    $wrapper = '$ErrorActionPreference = "Stop"' + "`n" + $invocation + "`n" +
        'if (Test-Path -LiteralPath variable:\LASTEXITCODE) { exit $LASTEXITCODE }'
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($wrapper))
    $output = @(& (Join-Path $PSHOME 'pwsh') -NoProfile -NonInteractive -EncodedCommand $encoded 2>&1) -join "`n"
    $code = $LASTEXITCODE
    if (($code -eq 0) -ne $success -or !$output.Contains($expectedText)) {
        throw "${name}: unexpected host result (exit $code): $output"
    }
    $script:passed++
}
try {
    Assert-Case 'partial implementation remains legitimately open' {}
    Assert-Case 'completed result cannot stay in open table' {
        Write-Fixture 'docs/REQUIREMENTS.md' $baseline['docs/REQUIREMENTS.md'].Replace('A2 | 부분 |', 'A2 | 완료 |')
    } 'open-status'
    Assert-Case 'dropped permanent number' {
        Write-Fixture 'docs/history/requirements-completed-index.md' ($baseline['docs/history/requirements-completed-index.md'] -replace '(?m)^\| <a id="a1"[^\n]*\n','')
    } 'requirement-id'
    Assert-Case 'duplicate registration' {
        Write-Fixture 'docs/history/requirements-completed-index.md' ($baseline['docs/history/requirements-completed-index.md'] + "`n| <a id=`"a2`"></a>A2 | 열림 | [Current](../REQUIREMENTS.md#a2) | Source |")
    } 'requirement-id'
    Assert-Case 'open state must agree with index' {
        Write-Fixture 'docs/history/requirements-completed-index.md' $baseline['docs/history/requirements-completed-index.md'].Replace('A2 | 열림 |','A2 | 완료 |')
    } 'status-reference'
    Assert-Case 'lost compatibility anchor breaks old links' {
        Write-Fixture 'docs/README.md' '[Old URL](REQUIREMENTS.md#a1)'
        Write-Fixture 'docs/REQUIREMENTS.md' $baseline['docs/REQUIREMENTS.md'].Replace('<a id="a1"></a>','')
    } 'local-link'
    Assert-Case 'missing local file' { Write-Fixture 'docs/README.md' '[Broken](missing.md)' } 'local-link'
    $paragraph = 'A unique observation must retain its boundary and source. ' * 4
    Assert-Case 'same-file duplicate paragraph' {
        Write-Fixture 'docs/README.md' ("# Map`n`n$paragraph`n`n$paragraph")
    } 'duplicate-text'
    Assert-Case 'cross-file duplicate paragraph' {
        Write-Fixture 'docs/README.md' ("# Map`n`n$paragraph")
        Write-Fixture 'AGENTS.md' ("# Entry`n`n$paragraph")
    } 'duplicate-text'
    Assert-Case 'duplicate long table row beside different rows' {
        $row = "| Input | $paragraph | Remaining |"
        Write-Fixture 'docs/DEVICE-CHECKS.md' ("# Device`n`n| Scope | Result | Limit |`n|---|---|---|`n$row`n| Other | Different | Pending |`n$row")
    } 'duplicate-text'
    Assert-Case 'handover release chronology' {
        Write-Fixture 'docs/HANDOVER.md' ($baseline['docs/HANDOVER.md'] + "`n- v1.0.0 old result.`n- v1.1.0 another old result.")
    } 'handover-history'
    Assert-Case 'handover growing item count' {
        Write-Fixture 'docs/HANDOVER.md' ($baseline['docs/HANDOVER.md'] + ((1..10 | ForEach-Object { "`n- Old item $_." }) -join ''))
    } 'handover-role'
    Assert-Case 'long completed detail in one handover item' {
        Write-Fixture 'docs/HANDOVER.md' ($baseline['docs/HANDOVER.md'] + "`n- " + ('Finished detail. ' * 50))
    } 'handover-role'
    Assert-Case 'completed requirement is no longer a selected next action' {
        Write-Fixture 'docs/HANDOVER.md' $baseline['docs/HANDOVER.md'].Replace('**A2 부분**','**A1 완료**')
    } 'handover-status'
    Assert-Case 'plain completed requirement cannot remain a selected next action' {
        Write-Fixture 'docs/HANDOVER.md' $baseline['docs/HANDOVER.md'].Replace('**A2 부분**','A1 완료')
    } 'handover-status'
    Assert-Case 'plain open requirement remains a valid selected next action' {
        Write-Fixture 'docs/HANDOVER.md' $baseline['docs/HANDOVER.md'].Replace('**A2 부분**','A2 부분')
    }
    Assert-Case 'context may refer to a completed requirement without selecting it' {
        Write-Fixture 'docs/HANDOVER.md' ($baseline['docs/HANDOVER.md'] + "`n- **Context**: A1 is an earlier result; A2 remains open.")
    }
    Assert-Case 'new selected coverage may grow registration and current rows' {
        $req = $baseline['docs/REQUIREMENTS.md'].Replace('**A2**','**A3**').Replace('## 이전 완료 링크 호환', "| <a id=`"a3`"></a>A3 | 보류 | New scope with distinct limits. | Await selected work. |`n`n## 이전 완료 링크 호환")
        Write-Fixture 'docs/REQUIREMENTS.md' $req
        Write-Fixture 'docs/history/requirements-completed-index.md' ($baseline['docs/history/requirements-completed-index.md'] + "`n| <a id=`"a3`"></a>A3 | 열림 | [Current](../REQUIREMENTS.md#a3) | Source |")
    }
    # Historical duplicates are not current-role violations. The generated private fixture must never be read.
    Assert-Case 'history and private documents are outside duplicate input' {
        Write-Fixture 'docs/history/old.md' ("$paragraph`n`n$paragraph")
        Write-Fixture '.codex/private.md' '[must not be scanned](missing-private.md)'
        Write-Fixture 'docs/.codex/private.md' '[must not be scanned](missing-private.md)'
    }
    Assert-Case 'explicit private link is rejected without opening its target' {
        Write-Fixture 'docs/README.md' '[Private](../.codex/private.md)'
    } 'private-path'
    # Share only the Git object database with a temporary repository: no commits, network or user identity changes.
    $repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    $objectPath = git -C $repository rev-parse --path-format=absolute --git-path objects
    $commit = git -C $repository rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve fixture Git evidence source.' }
    git -C $fixture init --quiet
    if ($LASTEXITCODE -ne 0) { throw 'Cannot initialize temporary evidence repository.' }
    [IO.File]::WriteAllText((Join-Path $fixture '.git/objects/info/alternates'), $objectPath.Replace('\','/') + "`n")
    $fixedSource = "https://github.com/zpstudios/kotu/blob/$commit/docs/HANDOVER.md"
    Assert-Case 'existing fixed evidence line' { Write-Fixture 'docs/README.md' "[Evidence]($fixedSource#L1)" }
    Assert-Case 'fixed evidence line out of range' { Write-Fixture 'docs/README.md' "[Evidence]($fixedSource#L999999)" } 'fixed-git-link'
    Assert-Case 'zero evidence line is invalid' { Write-Fixture 'docs/README.md' "[Evidence]($fixedSource#L0)" } 'fixed-git-link'
    Assert-Case 'missing historical path' {
        Write-Fixture 'docs/README.md' "[Evidence](https://github.com/zpstudios/kotu/blob/$commit/docs/no-such-evidence.md#L1)"
    } 'fixed-git-link'
    Assert-Case 'missing historical commit' {
        Write-Fixture 'docs/README.md' '[Evidence](https://github.com/zpstudios/kotu/blob/0000000000000000000000000000000000000000/docs/HANDOVER.md#L1)'
    } 'fixed-git-link'
    if (!$SkipHostExitTests) {
        $scriptPath = $PSCommandPath.Replace("'", "''")
        Assert-HostExit 'successful regressions must return success to the CI host' ". '$scriptPath' -SkipHostExitTests" $true 'behavior cases passed.'

        foreach ($file in $baseline.Keys) { Write-Fixture $file $baseline[$file] }
        Write-Fixture 'docs/README.md' '[Broken](missing.md)'
        $checkerPath = $checker.Replace("'", "''"); $rootPath = $fixture.Replace("'", "''")
        Assert-HostExit 'invalid documentation must fail the CI host' ". '$checkerPath' -Root '$rootPath'" $false '[local-link]'

        # A deliberately faulty checker proves that a regression assertion also reaches the host.
        Write-Fixture 'host-regression/eng/Test-Documentation.Tests.ps1' ([IO.File]::ReadAllText($PSCommandPath))
        Write-Fixture 'host-regression/eng/Test-Documentation.ps1' 'param([string]$Root) # Deliberately accepts every fixture.'
        $brokenSuite = (Join-Path $fixture 'host-regression/eng/Test-Documentation.Tests.ps1').Replace("'", "''")
        Assert-HostExit 'failed regression assertions must fail the CI host' ". '$brokenSuite' -SkipHostExitTests" $false 'did not fail for [open-status]'
    }
} finally {
    # Only the unique, resolved temporary fixture created above is eligible for recursive deletion.
    $resolved = [IO.Path]::GetFullPath($fixture)
    if (!$resolved.StartsWith($fixtureParent, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -notmatch '^kotu-doc-tests-[0-9a-f]{32}$') { throw 'Unsafe fixture cleanup path.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
# Expected failing Git probes are consumed by the assertions above. Publish success only
# after every assertion and cleanup succeeds; exceptions never reach this assignment.
$global:LASTEXITCODE = 0
Write-Output "Documentation checker: $passed behavior cases passed."
