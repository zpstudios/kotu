param(
    [string]$WorkflowPath = (Join-Path $PSScriptRoot '../.github/workflows/release.yml'),
    [string]$BuildWorkflowPath = (Join-Path $PSScriptRoot '../.github/workflows/build.yml')
)
$ErrorActionPreference = 'Stop'

# 이 검사는 현재의 단일 작업·순차 단계 형식을 보수적으로 고정한다.
# YAML 전체 문법 검사는 actionlint가 담당하며 형식 변경 시 이 계약도 함께 갱신한다.
$nodeSetup = 'uses: actions/setup-node@949feb2413d6458794dcd2491c4babbbce0c15c1'
$protocolTest = 'node src/KOTU.Module.Document/EditorSource/protocol.test.mjs'
$visualTest = 'node src/KOTU.Module.Document/EditorSource/visual.test.mjs'
function Get-WorkflowSteps([string]$text) {
    @([regex]::Split($text, '(?m)^      - name: ') | Select-Object -Skip 1)
}

function Assert-EditorGates([string]$text, [bool]$release) {
    $steps = @(Get-WorkflowSteps $text)
    $previous = -1
    if ([regex]::Matches($text, 'uses: actions/setup-node@').Count -ne 1) { throw 'Expected one pinned Node setup' }
    foreach ($command in @($nodeSetup, $protocolTest, $visualTest, 'dotnet restore KOTU.sln -p:Platform=x64',
        'dotnet build KOTU.sln -c Release -p:Platform=x64 --no-restore')) {
        $indices = @(for ($i = 0; $i -lt $steps.Count; $i++) { if ($steps[$i].Contains($command)) { $i } })
        if ($indices.Count -ne 1 -or $indices[0] -le $previous) { throw "Missing, combined or reordered editor gate: $command" }
        $step = $steps[$indices[0]]
        $conditions = [regex]::Matches($step, '(?m)^        if: (.+)\r?$')
        if (($release -and ($conditions.Count -ne 1 -or $conditions[0].Groups[1].Value.Trim() -ne "steps.ver.outputs.skip != 'true'")) -or
            (!$release -and $conditions.Count -ne 0) -or $step -match '(?m)^        continue-on-error:') {
            throw "Bypassable editor gate: $command"
        }
        if ($command -eq $nodeSetup) {
            if ($step -notmatch ('(?m)^        ' + [regex]::Escape($nodeSetup) + '(?: # v7)?\r?$') -or
                $step -notmatch '(?m)^          node-version: ''24''\r?$' -or
                $step -notmatch '(?m)^          package-manager-cache: false\r?$') { throw 'Node setup version/cache contract changed' }
        } elseif ($command -eq $protocolTest -or $command -eq $visualTest) {
            # 각 명령은 별도 단계의 한 줄이어야 첫 native 실패가 뒤 명령 성공에 가려지지 않는다.
            $runs = [regex]::Matches($step, '(?m)^        run: (.+)\r?$')
            if ($runs.Count -ne 1 -or $runs[0].Groups[1].Value.Trim() -ne $command -or
                $step -notmatch '(?m)^        shell: pwsh\r?$') { throw "Masked or modified editor test command: $command" }
        }
        $previous = $indices[0]
    }
}

function Assert-BuildWorkflow([string]$text) {
    if ([regex]::Matches($text, '(?m)^  [\w-]+:[ \t]*\r?$').Count -ne 4) {
        throw 'Expected push, pull_request, workflow_dispatch and one build job layout'
    }
    Assert-EditorGates $text $false
}

function Assert-ReleaseWorkflow([string]$text) {
    if ([regex]::Matches($text, '(?m)^  [\w-]+:[ \t]*\r?$').Count -ne 3) {
        throw 'Expected push, workflow_dispatch and one job layout'
    }
    if ($text -notmatch '(?m)^  group: release\r?$' -or
        $text -notmatch '(?m)^  cancel-in-progress: false\r?$') { throw 'Release serialization changed' }
    $steps = @(Get-WorkflowSteps $text)
    $gate = "steps.ver.outputs.skip != 'true'"
    $required = @(
        './eng/Test-Architecture.ps1',
        $nodeSetup,
        $protocolTest,
        $visualTest,
        'dotnet restore KOTU.sln -p:Platform=x64',
        'dotnet build KOTU.sln -c Release -p:Platform=x64 --no-restore',
        'dotnet test KOTU.sln -c Release -p:Platform=x64 --no-build',
        'dotnet run --project eng/ShellVerbSmoke/ShellVerbSmoke.csproj -c Release',
        'dotnet publish src/KOTU.App',
        './eng/Prepare-Package.ps1 -PackagePath artifacts/KOTU',
        './eng/Test-AppStartup.ps1 -PackagePath artifacts/KOTU',
        'vpk pack --packId KOTU',
        './eng/Test-ReleasePackage.ps1 -PackagePath vpk_out',
        './eng/Test-Installer.ps1 -SetupPath vpk_out/KOTU-win-Setup.exe -PackagePath artifacts/KOTU',
        './eng/Build-Standalone.ps1',
        './eng/Test-Standalone.ps1 -ExecutablePath vpk_out/KOTU-win-Standalone.exe',
        'uses: softprops/action-gh-release@v2'
    )
    $previous = -1
    foreach ($command in $required) {
        $indices = @(for ($i = 0; $i -lt $steps.Count; $i++) {
            if ($steps[$i].Contains($command)) { $i }
        })
        if ($indices.Count -ne 1 -or $indices[0] -lt $previous) { throw "Missing/reordered release gate: $command" }
        $step = $steps[$indices[0]]
        $expected = if ($command -eq 'uses: softprops/action-gh-release@v2') { "success() && $gate && github.ref == 'refs/heads/master'" } else { $gate }
        $condition = [regex]::Match($step, '(?m)^        if: (.+)\r?$').Groups[1].Value.Trim()
        if ($condition -ne $expected -or $step -match '(?m)^        continue-on-error:') {
            throw "Bypassable release gate: $command"
        }
        $previous = $indices[0]
    }
    if ([regex]::Matches($text, 'uses: softprops/action-gh-release@').Count -ne 1) { throw 'Unexpected publication count' }
    $download = $steps | Where-Object { $_.Contains('vpk download github') }
    if ($download -notmatch [regex]::Escape("if: $gate && github.ref == 'refs/heads/master'")) { throw 'Dry run must not download release assets' }
    if (!$text.Contains('if ($exists -and $env:GITHUB_REF -eq ''refs/heads/master'')')) { throw 'Version idempotence/dry run rule changed' }
    Assert-EditorGates $text $true
}

$source = Get-Content -LiteralPath $WorkflowPath -Raw
$buildSource = Get-Content -LiteralPath $BuildWorkflowPath -Raw
Assert-ReleaseWorkflow $source
Assert-BuildWorkflow $buildSource
# 실제 우회 회귀를 주입해 검사기가 실패를 잡는지도 확인한다.
$mutations = @(
    $source.Replace("success() && steps.ver.outputs.skip != 'true' && github.ref == 'refs/heads/master'", "steps.ver.outputs.skip != 'true'"),
    $source.Replace('dotnet test KOTU.sln', 'dotnet test tests/OneProject.csproj'),
    $source.Replace('run: dotnet test KOTU.sln', "continue-on-error: true`n        run: dotnet test KOTU.sln"),
    $source.Replace('./eng/Test-Installer.ps1 -SetupPath vpk_out', './eng/Skipped.ps1 -SetupPath vpk_out'),
    $source.Replace('dotnet run --project eng/ShellVerbSmoke/ShellVerbSmoke.csproj', 'dotnet run --project eng/Skipped.csproj'),
    $source.Replace('./eng/Build-Standalone.ps1', './eng/Skipped.ps1'),
    $source.Replace('./eng/Test-Standalone.ps1', './eng/Skipped.ps1')
)
foreach ($mutation in $mutations) {
    $rejected = $false
    try { Assert-ReleaseWorkflow $mutation } catch { $rejected = $true }
    if (!$rejected) { throw 'Release gate regression was not rejected' }
}

function Get-EditorGateMutations([string]$text, [bool]$release) {
    $steps = @(Get-WorkflowSteps $text)
    $setup = '      - name: ' + ($steps | Where-Object { $_.Contains($nodeSetup) })
    $protocol = '      - name: ' + ($steps | Where-Object { $_.Contains($protocolTest) })
    $visual = '      - name: ' + ($steps | Where-Object { $_.Contains($visualTest) })
    $wrongSetup = if ($release) { $setup.Replace("if: steps.ver.outputs.skip != 'true'", 'if: success()') }
        else { $setup.Replace('        uses:', "        if: github.event_name != 'pull_request'`n        uses:") }
    $wrongProtocol = if ($release) { $protocol.Replace("if: steps.ver.outputs.skip != 'true'", 'if: false') }
        else { $protocol.Replace('        shell:', "        if: github.event_name != 'pull_request'`n        shell:") }
    @(
        @{ Name = 'missing Node provisioning'; Text = $text.Replace($setup, '') },
        @{ Name = 'wrong Node condition'; Text = $text.Replace($setup, $wrongSetup) },
        @{ Name = 'Node provisioning after protocol'; Text = $text.Replace($setup, '__NODE_SWAP__').Replace($protocol, $setup).Replace('__NODE_SWAP__', $protocol) },
        @{ Name = 'missing protocol check'; Text = $text.Replace($protocol, '') },
        @{ Name = 'missing visual check'; Text = $text.Replace($visual, '') },
        @{ Name = 'ignored protocol failure'; Text = $text.Replace($protocol, $protocol.Replace('        run:', "        continue-on-error: true`n        run:")) },
        @{ Name = 'masked protocol exit'; Text = $text.Replace("run: $protocolTest", "run: $protocolTest; exit 0") },
        @{ Name = 'combined native commands'; Text = $text.Replace($visual, '').Replace("run: $protocolTest", "run: |`n          $protocolTest`n          $visualTest") },
        @{ Name = 'conditional protocol bypass'; Text = $text.Replace($protocol, $wrongProtocol) },
        @{ Name = 'unsupported Node version'; Text = $text.Replace("node-version: '24'", "node-version: '18'") },
        @{ Name = 'implicit package caching'; Text = $text.Replace('package-manager-cache: false', 'package-manager-cache: true') },
        @{ Name = 'visual before protocol'; Text = $text.Replace($protocol, '__TEST_SWAP__').Replace($visual, $protocol).Replace('__TEST_SWAP__', $visual) }
    )
}
$editorMutationCount = 0
foreach ($kind in @('release', 'build')) {
    $original = if ($kind -eq 'release') { $source } else { $buildSource }
    foreach ($mutation in @(Get-EditorGateMutations $original ($kind -eq 'release'))) {
        if ($mutation.Text -eq $original) { throw "Mutation did not change $kind workflow: $($mutation.Name)" }
        $rejected = $false
        try {
            if ($kind -eq 'release') { Assert-ReleaseWorkflow $mutation.Text } else { Assert-BuildWorkflow $mutation.Text }
        } catch { $rejected = $true }
        if (!$rejected) { throw "Editor gate regression not rejected in ${kind}: $($mutation.Name)" }
        $editorMutationCount++
    }
}
Write-Output "Release/build gate contracts passed; seven existing release and $editorMutationCount editor regressions rejected."

