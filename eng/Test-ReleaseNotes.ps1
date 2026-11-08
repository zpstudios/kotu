$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$generator = Join-Path $PSScriptRoot 'New-ReleaseBody.ps1'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('kotu-release-notes-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory((Join-Path $fixture 'docs')) | Out-Null
[IO.Directory]::CreateDirectory((Join-Path $fixture 'eng')) | Out-Null
$body = Join-Path $fixture 'body.md'
$note = Join-Path $fixture 'docs/RELEASE-v1.2.3.md'
$valid = "# v1.2.3 — Selection outline`n`nSelected tiles retain their outline after scrolling."
$cases = 0
function Assert-Rejected([string]$name, [scriptblock]$action) {
    # 이전 성공 산출물이 실패를 가리지 못하게 매 사례마다 별도 출력을 검사한다.
    if (Test-Path -LiteralPath $body) { Remove-Item -LiteralPath $body }
    $rejected = $false
    try { & $action | Out-Null } catch { $rejected = $true }
    if (!$rejected -or (Test-Path -LiteralPath $body)) { throw "Invalid release notes accepted: $name" }
    $script:cases++
}
try {
    & $generator -RepositoryRoot $repository -OutputPath $body | Out-Null
    $currentBody = Get-Content -LiteralPath $body -Raw
    if ($currentBody -notmatch '(?m)^## Choose a download\r?$' -or
        $currentBody -match '앱 이름이[^\r\n]*바뀌었습니다[^\r\n]*v0\.86\.0|이번에는[^\r\n]*이관되지|v0\.88\.0\*\*: 실행 파일 이름이') {
        throw 'Current body must contain current download guidance without the obsolete migration announcement'
    }
    Set-Content -LiteralPath (Join-Path $fixture 'Directory.Build.props') -Value '<Project><PropertyGroup><Version>1.2.3</Version></PropertyGroup></Project>' -Encoding utf8
    $downloads = [IO.File]::ReadAllText((Join-Path $repository 'eng/Release-Downloads.md')).Replace("`r`n", "`n")
    # Windows 체크아웃의 CRLF와 로컬 LF를 실제 생성 입력으로 모두 검사한다.
    foreach ($lineEnding in @("`n", "`r`n")) {
        $validInput = $valid.Replace("`n", $lineEnding)
        [IO.File]::WriteAllText($note, $validInput)
        [IO.File]::WriteAllText((Join-Path $fixture 'eng/Release-Downloads.md'), $downloads.Replace("`n", $lineEnding))
        & $generator -RepositoryRoot $fixture -Version '1.2.3' -OutputPath $body | Out-Null
        $generated = Get-Content -LiteralPath $body -Raw
        if (!$generated.StartsWith($validInput) -or [regex]::Matches($generated, '(?m)^## Choose a download\r?$').Count -ne 1) {
            throw 'Versioned changes and download guidance must each appear once for LF and CRLF inputs'
        }
    }
    Assert-Rejected 'wrong requested version' { & $generator -RepositoryRoot $fixture -Version '1.2.4' -OutputPath $body }
    Remove-Item -LiteralPath $note
    Assert-Rejected 'missing current note' { & $generator -RepositoryRoot $fixture -OutputPath $body }
    foreach ($case in @(
        @{ Name = 'wrong title version'; Text = $valid.Replace('v1.2.3', 'v1.2.2') },
        @{ Name = 'version prefix collision'; Text = $valid.Replace('v1.2.3', 'v1.2.30') },
        @{ Name = 'empty file'; Text = '' },
        @{ Name = 'whitespace file'; Text = " `r`n`t" },
        @{ Name = 'title only'; Text = '# v1.2.3' },
        @{ Name = 'headings only'; Text = "# v1.2.3`n`n## Changes`n---" },
        @{ Name = 'comments only'; Text = "# v1.2.3`n<!-- Explain the actual changes here. -->" },
        @{ Name = 'unclosed comment only'; Text = "# v1.2.3`n<!-- Explain the actual changes here." },
        @{ Name = 'TODO placeholder'; Text = "# v1.2.3`nTODO: describe changes" },
        @{ Name = 'TBD placeholder'; Text = "# v1.2.3`nTBD" },
        @{ Name = 'placeholder marker'; Text = "# v1.2.3`nPlaceholder release notes" },
        @{ Name = 'coming soon placeholder'; Text = "# v1.2.3`nComing soon" },
        @{ Name = 'Korean placeholder'; Text = "# v1.2.3`n변경 사항은 추후 작성" }
    )) {
        Set-Content -LiteralPath $note -Value $case.Text -Encoding utf8
        Assert-Rejected $case.Name { & $generator -RepositoryRoot $fixture -OutputPath $body }
    }
    Set-Content -LiteralPath $note -Value $valid -Encoding utf8
    Set-Content -LiteralPath (Join-Path $fixture 'Directory.Build.props') -Value '<Project><PropertyGroup><Version>1.2.4</Version></PropertyGroup></Project>' -Encoding utf8
    Assert-Rejected 'future version without new notes' { & $generator -RepositoryRoot $fixture -OutputPath $body }
    Write-Output "Current release body and LF/CRLF synthetic bodies passed; $cases invalid release-note cases rejected."
} finally {
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $resolved = [IO.Path]::GetFullPath($fixture)
    if (!$resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -notmatch '^kotu-release-notes-[0-9a-f]{32}$') { throw 'Unexpected test fixture cleanup path' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
