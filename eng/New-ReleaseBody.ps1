param(
    [string]$RepositoryRoot = (Split-Path $PSScriptRoot -Parent),
    [string]$Version,
    [Parameter(Mandatory = $true)][string]$OutputPath
)
$ErrorActionPreference = 'Stop'

$versions = @(([xml](Get-Content -LiteralPath (Join-Path $RepositoryRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version |
    Where-Object { $_ })
if ($versions.Count -ne 1 -or $versions[0] -notmatch '^\d+\.\d+\.\d+$') { throw 'Expected one release version in Directory.Build.props' }
$current = [string]$versions[0]
if ($Version -and $Version -ne $current) { throw "Requested release version $Version does not match $current" }
$notePath = Join-Path $RepositoryRoot "docs/RELEASE-v$current.md"
if (!(Test-Path -LiteralPath $notePath -PathType Leaf)) { throw "Release notes missing: $notePath" }
$notes = ([string](Get-Content -LiteralPath $notePath -Raw)).Trim()
$lines = $notes -split '\r?\n', 2
if (!$notes -or $lines[0] -notmatch ('^# v' + [regex]::Escape($current) + '(?:\s+[—–-]\s+\S.*)?$')) {
    throw "Release notes must start with '# v$current' and an optional title"
}
$content = if ($lines.Count -gt 1) { $lines[1] } else { '' }
# 주석·제목·구분선만 있는 파일은 실제 변경 설명이 아니다.
$content = [regex]::Replace($content, '(?s)<!--.*?(?:-->|$)', '')
$content = [regex]::Replace($content, '(?m)^\s*(?:#{1,6}\s.*|[-*_\s]+)\r?$', '')
if ($content -notmatch '\p{L}{2}') { throw 'Release notes have no change description' }
if ($notes -match '(?i)\b(?:TODO|TBD|TBA|FIXME|PLACEHOLDER|WIP)\b|\bcoming soon\b|\bto be (?:written|added)\b|작성\s*예정|추후\s*작성|미작성|여기에\s*작성') {
    throw 'Release notes contain unfinished placeholder text'
}
$downloadsPath = Join-Path $RepositoryRoot 'eng/Release-Downloads.md'
$downloads = Get-Content -LiteralPath $downloadsPath -Raw
if ([string]::IsNullOrWhiteSpace($downloads)) { throw 'Download guidance is empty' }
$output = [IO.Path]::GetFullPath($OutputPath)
if ($output -eq [IO.Path]::GetFullPath($notePath) -or $output -eq [IO.Path]::GetFullPath($downloadsPath)) {
    throw 'Generated body must not overwrite its inputs'
}
[IO.Directory]::CreateDirectory((Split-Path $output -Parent)) | Out-Null
[IO.File]::WriteAllText($output, $notes + "`n`n" + $downloads.Trim() + "`n", [Text.UTF8Encoding]::new($false))
Write-Output "Validated release v$current body: $output"
