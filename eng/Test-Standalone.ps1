param([Parameter(Mandatory)][string]$ExecutablePath)
$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path -LiteralPath $ExecutablePath).Path
. (Join-Path $PSScriptRoot 'StandaloneSnapshots.ps1')
$beforeFiles = @(Get-AppDataSnapshot | Sort-Object)
$beforeRegistry = @(Get-RegistrySnapshot | Sort-Object)
$tempBase = if ($env:KOTU_STANDALONE_TEMP_BASE) { $env:KOTU_STANDALONE_TEMP_BASE } else { $env:TEMP }
$beforeTemp = @(Get-ChildItem -LiteralPath $tempBase -Directory -Filter 'KOTU-Standalone-*' | ForEach-Object FullName | Sort-Object)
$process = Start-Process -FilePath $exe -ArgumentList '--standalone-smoke-test' -WindowStyle Hidden -PassThru
try {
    if (!$process.WaitForExit(120000)) { throw 'Standalone startup/cleanup timed out' }
    if ($process.ExitCode -ne 0) { throw "Standalone smoke failed: $($process.ExitCode)" }
} finally {
    if (!$process.HasExited) { Stop-Process -Id $process.Id -Force }
    $process.Dispose()
}
$afterFiles = @(Get-AppDataSnapshot | Sort-Object)
if (($beforeFiles -join "`n") -cne ($afterFiles -join "`n")) {
    $changes = @(Compare-Object $beforeFiles $afterFiles | ForEach-Object {
        $_.InputObject -replace ':[A-F0-9]{64}$', ''
    } | Select-Object -Unique)
    throw ('KOTU AppData changed during standalone verification: ' + ($changes -join ', '))
}
if (($beforeRegistry -join "`n") -cne (@(Get-RegistrySnapshot | Sort-Object) -join "`n")) { throw 'Standalone changed product registry keys' }
$afterTemp = @(Get-ChildItem -LiteralPath $tempBase -Directory -Filter 'KOTU-Standalone-*' | ForEach-Object FullName | Sort-Object)
if (($beforeTemp -join "`n") -cne ($afterTemp -join "`n")) { throw 'Standalone left its session extraction folder' }
Write-Output 'Standalone WinUI startup, memory settings, disabled updates, AppData/registry preservation and normal-exit cleanup passed.'
