param(
    [string]$DotnetPath = 'dotnet',
    [string]$SevenZipPath = 'C:/Program Files/7-Zip/7z.dll',
    [string]$VcRuntimePath,
    [string]$OutputPath = (Join-Path $PSScriptRoot '../vpk_out/KOTU-win-Standalone.exe')
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'StandalonePackaging.ps1')
$runtime = Find-StandaloneVcRuntime $VcRuntimePath
$repository = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$build = Join-Path $repository ('artifacts/standalone-build/' + [Guid]::NewGuid().ToString('N'))
$payload = Join-Path $build 'payload'
$zipPath = Join-Path $build 'payload.zip'
$version = ([xml](Get-Content (Join-Path $repository 'Directory.Build.props'))).Project.PropertyGroup.Version |
    Where-Object { $_ } | Select-Object -First 1
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid standalone version' }
if (!(Test-Path -LiteralPath $SevenZipPath)) { throw '7z.dll is required for standalone packaging' }
New-Item -ItemType Directory -Path $build -Force | Out-Null
# The payload is a separate publish. Never use the Velopack pack directory or a .NET bundle.
& $DotnetPath publish (Join-Path $repository 'src/KOTU.App') -c Release -p:Platform=x64 -r win-x64 --self-contained `
    -p:KotuStandalone=true -p:PublishSingleFile=false -o $payload
if ($LASTEXITCODE -ne 0) { throw 'Standalone publish failed' }
Copy-Item -LiteralPath $SevenZipPath -Destination $payload
Copy-StandaloneVcRuntime $runtime $payload
foreach ($file in @('LICENSE', 'THIRD-PARTY-NOTICES.md')) {
    Copy-Item -LiteralPath (Join-Path $repository $file) -Destination $payload
}
Copy-Item -LiteralPath (Join-Path $repository 'packaging/standalone-policy.txt') -Destination $payload
Copy-Item -LiteralPath (Join-Path $repository 'packaging/standalone-vc-runtime-notice.txt') -Destination (Join-Path $payload 'licenses/Microsoft-VC-Runtime-NOTICE.txt')
foreach ($file in @('KOTU.exe', 'KOTU.Core.dll', 'resources.pri', '7z.dll', 'ScreenRecorderLib.dll',
                    'Assets/app.ico', 'Assets/test-clip.mp4', 'Assets/sample.mp3', 'licenses/ScreenRecorderLib-LICENSE.txt')) {
    if (!(Test-Path -LiteralPath (Join-Path $payload $file))) { throw "Standalone payload missing: $file" }
}
$vlc = Get-ChildItem -LiteralPath $payload -Recurse -Filter libvlc.dll | Select-Object -First 1
if (!$vlc -or !(Test-Path (Join-Path $vlc.DirectoryName 'libvlccore.dll')) -or
    !(Test-Path (Join-Path $vlc.DirectoryName 'plugins'))) { throw 'Standalone VLC engine/plugins missing' }
if (Get-ChildItem -LiteralPath $payload -Recurse -File | Where-Object { $_.Name -in @('Update.exe', 'sq.version', 'releases.win.json') }) {
    throw 'Velopack metadata must not be included in the standalone payload'
}
Add-Type -AssemblyName System.IO.Compression
$output = [IO.File]::Open($zipPath, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
try {
    $zip = [IO.Compression.ZipArchive]::new($output, [IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        foreach ($file in Get-ChildItem -LiteralPath $payload -Recurse -File -Force | Where-Object Extension -ne '.pdb') {
            if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Reparse point in standalone payload' }
            $name = [IO.Path]::GetRelativePath($payload, $file.FullName).Replace('\', '/')
            $entry = $zip.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
            $destination = $entry.Open()
            $source = [IO.File]::OpenRead($file.FullName)
            try { $source.CopyTo($destination) } finally { $source.Dispose(); $destination.Dispose() }
        }
    } finally { $zip.Dispose() }
} finally { $output.Dispose() }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw 'Windows .NET Framework compiler unavailable' }
$versionSource = Join-Path $build 'AssemblyVersion.cs'
# Generated build metadata; product source remains shared with the installed distribution.
[IO.File]::WriteAllText($versionSource, "[assembly: System.Reflection.AssemblyVersion(`"$version.0`")]")
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($OutputPath)) -Force | Out-Null
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /warnaserror+ `
    "/out:$OutputPath" "/win32icon:$(Join-Path $repository 'src/KOTU.App/Assets/app.ico')" `
    /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll `
    "/resource:$zipPath,KOTU.Payload.zip" (Join-Path $repository 'eng/StandaloneLauncher.cs') $versionSource
if ($LASTEXITCODE -ne 0) { throw 'Standalone launcher compilation failed' }
& (Join-Path $PSScriptRoot 'Test-StandaloneHelpers.ps1') -ExecutablePath $OutputPath -PayloadPath $payload
Write-Output "Standalone executable: $OutputPath ($((Get-Item -LiteralPath $OutputPath).Length) bytes)"
