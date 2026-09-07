param([string]$WorkflowPath = (Join-Path $PSScriptRoot '../.github/workflows/release.yml'))
$ErrorActionPreference = 'Stop'

# 이 검사는 현재의 단일 작업·순차 단계 형식을 보수적으로 고정한다.
# YAML 전체 문법 검사는 actionlint가 담당하며 형식 변경 시 이 계약도 함께 갱신한다.
function Assert-ReleaseWorkflow([string]$text) {
    if ([regex]::Matches($text, '(?m)^  [\w-]+:[ \t]*\r?$').Count -ne 3) {
        throw 'Expected push, workflow_dispatch and one job layout'
    }
    if ($text -notmatch '(?m)^  group: release\r?$' -or
        $text -notmatch '(?m)^  cancel-in-progress: false\r?$') { throw 'Release serialization changed' }
    $steps = @([regex]::Split($text, '(?m)^      - name: ') | Select-Object -Skip 1)
    $gate = "steps.ver.outputs.skip != 'true'"
    $required = @(
        './eng/Test-Architecture.ps1',
        'dotnet restore KOTU.sln -p:Platform=x64',
        'dotnet build KOTU.sln -c Release -p:Platform=x64 --no-restore',
        'dotnet test KOTU.sln -c Release -p:Platform=x64 --no-build',
        'dotnet publish src/KOTU.App',
        './eng/Prepare-Package.ps1 -PackagePath artifacts/KOTU',
        './eng/Test-AppStartup.ps1 -PackagePath artifacts/KOTU',
        'vpk pack --packId KOTU',
        './eng/Test-ReleasePackage.ps1 -PackagePath vpk_out',
        './eng/Test-Installer.ps1 -SetupPath vpk_out/KOTU-win-Setup.exe -PackagePath artifacts/KOTU',
        'uses: softprops/action-gh-release@v2'
    )
    $previous = -1
    foreach ($command in $required) {
        $indices = @(for ($i = 0; $i -lt $steps.Count; $i++) {
            if ($steps[$i].Contains($command)) { $i }
        })
        if ($indices.Count -ne 1 -or $indices[0] -lt $previous) { throw "Missing/reordered release gate: $command" }
        $step = $steps[$indices[0]]
        $expected = if ($command.StartsWith('uses:')) { "success() && $gate && github.ref == 'refs/heads/master'" } else { $gate }
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
}

$source = Get-Content -LiteralPath $WorkflowPath -Raw
Assert-ReleaseWorkflow $source
# 실제 우회 회귀를 주입해 검사기가 실패를 잡는지도 확인한다.
$mutations = @(
    $source.Replace("success() && steps.ver.outputs.skip != 'true' && github.ref == 'refs/heads/master'", "steps.ver.outputs.skip != 'true'"),
    $source.Replace('dotnet test KOTU.sln', 'dotnet test tests/OneProject.csproj'),
    $source.Replace('run: dotnet test KOTU.sln', "continue-on-error: true`n        run: dotnet test KOTU.sln"),
    $source.Replace('./eng/Test-Installer.ps1 -SetupPath vpk_out', './eng/Skipped.ps1 -SetupPath vpk_out')
)
foreach ($mutation in $mutations) {
    $rejected = $false
    try { Assert-ReleaseWorkflow $mutation } catch { $rejected = $true }
    if (!$rejected) { throw 'Release gate regression was not rejected' }
}
Write-Output 'Release gate contract passed; four bypass regressions rejected.'

