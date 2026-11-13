[CmdletBinding()]
param(
    [string]$EnginePath = (Join-Path $PSScriptRoot '../artifacts/recording-engine/ScreenRecorderLib.dll'),
    [string]$PublishedPath,
    [switch]$CheckSourcesOnly
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$vendor = Join-Path $repository 'vendor/ScreenRecorderLib'
$manifestPath = Join-Path $vendor 'source-manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
foreach ($file in $manifest.Files) {
    $source = [IO.Path]::GetFullPath((Join-Path $vendor $file.Path))
    if (!$source.StartsWith([IO.Path]::GetFullPath($vendor) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid recording engine manifest path.' }
    if (!(Test-Path -LiteralPath $source) -or (Get-FileHash -LiteralPath $source).Hash -ne $file.SHA256) { throw "Recording engine source differs from its manifest: $($file.Path)" }
}
if ($CheckSourcesOnly) { Write-Output "Recording engine source verified: $($manifest.Files.Count) files."; return }
if (!(Test-Path -LiteralPath $EnginePath)) { throw 'Patched recording engine is missing. Run eng/Build-RecordingEngine.ps1 before building KOTU.' }
$receiptPath = Join-Path (Split-Path ([IO.Path]::GetFullPath($EnginePath)) -Parent) 'build-receipt.json'
if (!(Test-Path -LiteralPath $receiptPath)) { throw 'Recording engine build receipt is missing. Run eng/Build-RecordingEngine.ps1.' }
$receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
if ($receipt.SourceManifestSHA256 -ne (Get-FileHash -LiteralPath $manifestPath).Hash -or
    $receipt.BuildScriptSHA256 -ne (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'Build-RecordingEngine.ps1')).Hash -or
    $receipt.TestScriptSHA256 -ne (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'Test-RecordingEngineNative.ps1')).Hash -or
    $receipt.TestSourceSHA256 -ne (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'recording-engine-tests/control-flow-tests.cpp')).Hash) { throw 'Recording engine sources or build procedure changed. Rebuild the recording engine.' }
if ((Get-FileHash -LiteralPath $EnginePath).Hash -ne $receipt.EngineSHA256 -or
    [Reflection.AssemblyName]::GetAssemblyName([IO.Path]::GetFullPath($EnginePath)).FullName -ne $manifest.AssemblyIdentity) { throw 'Recording engine does not match its verified build receipt.' }
if ($PublishedPath) {
    if (!(Test-Path -LiteralPath $PublishedPath) -or (Get-FileHash -LiteralPath $PublishedPath).Hash -ne $receipt.EngineSHA256) { throw 'Published recording engine differs from the patched build.' }
    $license = Join-Path (Split-Path ([IO.Path]::GetFullPath($PublishedPath)) -Parent) 'licenses/ScreenRecorderLib-LICENSE.txt'
    if (!(Test-Path -LiteralPath $license) -or (Get-FileHash -LiteralPath $license).Hash -ne (Get-FileHash -LiteralPath (Join-Path $vendor 'LICENSE')).Hash) { throw 'Published recording engine license is missing or changed.' }
}
Write-Output 'Patched recording engine verified.'
