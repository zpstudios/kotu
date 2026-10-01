param([Parameter(Mandatory)][string]$ExecutablePath, [Parameter(Mandatory)][string]$PayloadPath)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'StandalonePackaging.ps1')
. (Join-Path $PSScriptRoot 'StandaloneSnapshots.ps1')
$exe = (Resolve-Path -LiteralPath $ExecutablePath).Path
$payload = (Resolve-Path -LiteralPath $PayloadPath).Path
$artifacts = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts'))
$fixture = Join-Path $artifacts ('standalone-helper-tests/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
try {
    $imports = @(Get-StandalonePeImports (Join-Path $payload 'ScreenRecorderLib.dll'))
    foreach ($required in @('MSVCP140.dll', 'CONCRT140.dll', 'VCRUNTIME140.dll', 'VCRUNTIME140_1.dll')) {
        if ($required -notin $imports) { throw "Recorder import audit missed $required" }
    }
    Assert-StandaloneVcImports $payload
    $missing = Join-Path $fixture 'missing-crt'
    New-Item -ItemType Directory -Path $missing | Out-Null
    Copy-Item -LiteralPath (Join-Path $payload 'ScreenRecorderLib.dll') -Destination $missing
    Get-ChildItem -LiteralPath $payload -File | Where-Object Name -match '^(msvcp|concrt|vcruntime|vccorlib|vcamp|vcomp)14.*\.dll$' |
        Where-Object Name -ne 'concrt140.dll' | Copy-Item -Destination $missing
    $rejected = $false
    try { Assert-StandaloneVcImports $missing } catch { $rejected = $_.Exception.Message -match 'concrt140.dll' }
    if (!$rejected) { throw 'Missing native CRT dependency was accepted' }
    Copy-Item -LiteralPath (Join-Path $payload 'concrt140.dll') -Destination $missing
    # Mutate only a disposable PE fixture to require an additional VC dependency while all
    # four fixed presence checks still pass. The import-table audit must reject it.
    $engineFixture = Join-Path $missing 'ScreenRecorderLib.dll'
    $engineBytes = [IO.File]::ReadAllBytes($engineFixture)
    $importOffset = [Text.Encoding]::ASCII.GetString($engineBytes).IndexOf('VCRUNTIME140_1.dll', [StringComparison]::Ordinal)
    if ($importOffset -lt 0) { throw 'Import regression fixture could not be created' }
    $changedImport = [Text.Encoding]::ASCII.GetBytes('VCRUNTIME140_X.dll')
    [Buffer]::BlockCopy($changedImport, 0, $engineBytes, $importOffset, $changedImport.Length)
    [IO.File]::WriteAllBytes($engineFixture, $engineBytes)
    $rejected = $false
    try { Assert-StandaloneVcImports $missing } catch { $rejected = $_.Exception.Message -match 'transitive dependency missing: VCRUNTIME140_X.dll' }
    if (!$rejected) { throw 'Missing transitive native CRT dependency was accepted' }
    $rejected = $false
    try { Copy-StandaloneVcRuntime (Join-Path $env:WINDIR 'System32') $missing } catch { $rejected = $_.Exception.Message -match 'redistribution source' }
    if (!$rejected) { throw 'Windows System32 was accepted as a redistribution source' }

    $before = @(Get-AppDataSnapshot -BaseFolders $fixture | Sort-Object)
    New-Item -ItemType Directory -Path (Join-Path $fixture 'KOTU') | Out-Null
    $after = @(Get-AppDataSnapshot -BaseFolders $fixture | Sort-Object)
    if (($before -join "`n") -ceq ($after -join "`n")) { throw 'Empty AppData root trace was missed' }
    foreach ($name in @('KOTU', 'KOTU.archive.zip', 'ZP.Compress', 'WinUtil.ExtractHere')) {
        if (!(Test-StandaloneProductName $name)) { throw "Product registry identifier was excluded: $name" }
    }
    foreach ($name in @('KOTUOther', 'ZPrinter', 'Microsoft', 'NotKOTU', '.zip')) {
        if (Test-StandaloneProductName $name) { throw "Unrelated registry identifier was included: $name" }
    }

    $launcher = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($exe)).GetType('StandaloneLauncher')
    $receipt = $launcher.GetMethod('HasSmokeReceipt', [Reflection.BindingFlags]'NonPublic,Static')
    $nonce = [Guid]::NewGuid().ToString('N')
    $marker = Join-Path $fixture 'receipt.complete'
    if ($receipt.Invoke($null, @($marker.PSObject.BaseObject, $nonce.PSObject.BaseObject))) { throw 'Missing success receipt was accepted' }
    [IO.File]::WriteAllText($marker, "KOTU standalone smoke complete:$nonce")
    if (!$receipt.Invoke($null, @($marker.PSObject.BaseObject, $nonce.PSObject.BaseObject))) { throw 'Valid success receipt was rejected' }
    $wrongNonce = [Guid]::NewGuid().ToString('N')
    if ($receipt.Invoke($null, @($marker.PSObject.BaseObject, $wrongNonce.PSObject.BaseObject))) { throw 'Wrong nonce receipt was accepted' }
    [IO.File]::WriteAllText($marker, ('x' * 257))
    if ($receipt.Invoke($null, @($marker.PSObject.BaseObject, $nonce.PSObject.BaseObject))) { throw 'Oversized receipt was accepted' }
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $payload 'KOTU.exe'), '--standalone-smoke-test')
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.Environment.Remove('KOTU_STANDALONE_SMOKE_NONCE') | Out-Null
    $start.Environment.Remove('KOTU_STANDALONE_SMOKE_MARKER') | Out-Null
    $child = [Diagnostics.Process]::Start($start)
    try {
        if (!$child.WaitForExit(10000)) { throw 'Unattested smoke flag was not rejected promptly' }
        if ($child.ExitCode -ne 1) { throw 'Unattested smoke flag was accepted by the extracted application' }
    }
    finally {
        if (!$child.HasExited) { $child.Kill($true); $child.WaitForExit() }
        $child.Dispose()
    }
    Write-Output 'Standalone regressions passed: PE imports, missing/transitive CRT, prohibited system source, empty AppData root, registry ownership filters, nonce receipts and unattested CLI rejection.'
}
finally {
    $resolvedFixture = [IO.Path]::GetFullPath($fixture)
    if (!$resolvedFixture.StartsWith($artifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid standalone test cleanup path' }
    Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
}
