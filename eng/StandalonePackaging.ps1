# Packaging helpers are shared with the standalone regression checks.
function Get-StandalonePeImports([string]$Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 64 -or [BitConverter]::ToUInt16($bytes, 0) -ne 0x5a4d) { throw "Invalid PE file: $Path" }
    $pe = [BitConverter]::ToInt32($bytes, 60)
    if ($pe -lt 0 -or $pe + 264 -gt $bytes.Length -or [BitConverter]::ToUInt32($bytes, $pe) -ne 0x4550 -or
        [BitConverter]::ToUInt16($bytes, $pe + 4) -ne 0x8664) { throw "Expected x64 PE file: $Path" }
    $optional = $pe + 24
    if ([BitConverter]::ToUInt16($bytes, $optional) -ne 0x20b) { throw "Expected PE32+ file: $Path" }
    $sectionStart = $optional + [BitConverter]::ToUInt16($bytes, $pe + 20)
    $sections = @(for ($index = 0; $index -lt [BitConverter]::ToUInt16($bytes, $pe + 6); $index++) {
        $offset = $sectionStart + $index * 40
        if ($offset + 40 -gt $bytes.Length) { throw "Invalid PE sections: $Path" }
        [pscustomobject]@{ Rva = [BitConverter]::ToUInt32($bytes, $offset + 12)
            Size = [Math]::Max([BitConverter]::ToUInt32($bytes, $offset + 8), [BitConverter]::ToUInt32($bytes, $offset + 16))
            Raw = [BitConverter]::ToUInt32($bytes, $offset + 20) }
    })
    $mapRva = {
        param([uint32]$Rva)
        foreach ($section in $sections) {
            if ($Rva -ge $section.Rva -and $Rva -lt $section.Rva + $section.Size) {
                $mapped = [long]$section.Raw + $Rva - $section.Rva
                if ($mapped -ge $bytes.Length) { break }
                return [int]$mapped
            }
        }
        throw "Invalid PE import RVA: $Path"
    }
    $importRva = [BitConverter]::ToUInt32($bytes, $optional + 120)
    if ($importRva -eq 0) { return }
    $descriptor = & $mapRva $importRva
    while ($descriptor + 20 -le $bytes.Length) {
        $nameRva = [BitConverter]::ToUInt32($bytes, $descriptor + 12)
        if ($nameRva -eq 0) { return }
        $start = & $mapRva $nameRva
        $end = $start
        while ($end -lt $bytes.Length -and $bytes[$end] -ne 0) { $end++ }
        if ($end -eq $bytes.Length) { throw "Unterminated PE import: $Path" }
        [Text.Encoding]::ASCII.GetString($bytes, $start, $end - $start)
        $descriptor += 20
    }
    throw "Unterminated PE import table: $Path"
}

function Find-StandaloneVcRuntime([string]$ExplicitPath) {
    if ($ExplicitPath) { return (Resolve-Path -LiteralPath $ExplicitPath).Path }
    $bases = @()
    if ($env:VCToolsRedistDir) { $bases += $env:VCToolsRedistDir }
    if ($env:VCINSTALLDIR) { $bases += Join-Path $env:VCINSTALLDIR 'Redist/MSVC' }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (Test-Path -LiteralPath $vswhere) {
        $installations = & $vswhere -all -products '*' -requires Microsoft.VisualStudio.Component.VC.Redist.14.Latest -property installationPath
        if ($LASTEXITCODE -ne 0) { throw 'Visual Studio redistributable discovery failed' }
        $bases += @($installations | ForEach-Object { Join-Path $_ 'VC/Redist/MSVC' })
    }
    # The hosted Windows runner also exposes VS installations at these standard locations.
    foreach ($programFolder in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        $studioRoot = Join-Path $programFolder 'Microsoft Visual Studio'
        if (Test-Path -LiteralPath $studioRoot) {
            foreach ($year in Get-ChildItem -LiteralPath $studioRoot -Directory) {
                foreach ($edition in Get-ChildItem -LiteralPath $year.FullName -Directory) {
                    $bases += Join-Path $edition.FullName 'VC/Redist/MSVC'
                }
            }
        }
    }
    $candidates = @($bases | Select-Object -Unique | Where-Object { Test-Path -LiteralPath $_ } | ForEach-Object {
        Get-ChildItem -LiteralPath $_ -Directory -Recurse -Filter 'Microsoft.VC14*.CRT' |
            Where-Object { $_.Parent.Name -eq 'x64' -and (Test-Path -LiteralPath (Join-Path $_.FullName 'vcruntime140_1.dll')) }
    })
    $chosen = $candidates | Sort-Object { (Get-Item -LiteralPath (Join-Path $_.FullName 'vcruntime140.dll')).VersionInfo.FileVersionRaw } -Descending | Select-Object -First 1
    if (!$chosen) {
        throw 'Microsoft x64 VC14x CRT redistribution directory unavailable. Install the Visual Studio C++ Redistributable SDK component or pass -VcRuntimePath to its licensed x64 Microsoft.VC14x.CRT directory. System32 is not a redistribution source.'
    }
    return $chosen.FullName
}

function Assert-StandaloneVcImports([string]$Payload) {
    foreach ($file in @('msvcp140.dll', 'concrt140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll')) {
        if (!(Test-Path -LiteralPath (Join-Path $Payload $file))) { throw "Standalone VC runtime missing: $file" }
    }
    $engines = @(Get-Item -LiteralPath (Join-Path $Payload 'ScreenRecorderLib.dll'))
    $engines += @(Get-ChildItem -LiteralPath $Payload -File | Where-Object Name -match '^(msvcp|concrt|vcruntime|vccorlib|vcamp|vcomp)14.*\.dll$')
    foreach ($engine in $engines) {
        foreach ($dependency in Get-StandalonePeImports $engine.FullName) {
            if ($dependency -match '^(msvcp|concrt|vcruntime|vccorlib|vcamp|vcomp)14.*\.dll$' -and
                !(Test-Path -LiteralPath (Join-Path $Payload $dependency))) {
                throw "Standalone VC transitive dependency missing: $dependency (imported by $($engine.Name))"
            }
        }
    }
}

function Copy-StandaloneVcRuntime([string]$RuntimePath, [string]$Payload) {
    $source = (Resolve-Path -LiteralPath $RuntimePath).Path
    if ($source.StartsWith([IO.Path]::GetFullPath($env:WINDIR) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Windows system runtime files cannot be used as the VC redistribution source'
    }
    $files = @(Get-ChildItem -LiteralPath $source -File -Filter '*.dll')
    if (!$files.Count) { throw "VC redistribution directory has no DLLs: $source" }
    foreach ($file in $files) {
        $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
        if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
            throw "Expected a signed Microsoft redistribution file: $($file.FullName)"
        }
        $null = @(Get-StandalonePeImports $file.FullName)
        Copy-Item -LiteralPath $file.FullName -Destination $Payload
    }
    Assert-StandaloneVcImports $Payload
    Write-Output "App-local Microsoft VC runtime: $source ($($files.Count) signed x64 DLLs)"
}
