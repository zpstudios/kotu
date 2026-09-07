param([Parameter(Mandatory)][string]$PackagePath)
$ErrorActionPreference = 'Stop'
$package = (Resolve-Path -LiteralPath $PackagePath).Path
$sevenZip = 'C:/Program Files/7-Zip/7z.dll'
if (!(Test-Path $sevenZip)) { throw '7z.dll missing from Windows runner' }
Copy-Item $sevenZip $package
Copy-Item LICENSE, THIRD-PARTY-NOTICES.md $package
$required = @('KOTU.exe', 'resources.pri', '7z.dll',
              'KOTU.DocumentModel.dll', 'KOTU.FileOperations.dll',
              'LICENSE', 'THIRD-PARTY-NOTICES.md', 'Assets/app.ico', 'Assets/test-clip.mp4', 'Assets/sample.mp3')
foreach ($file in $required) {
  if (!(Test-Path (Join-Path $package $file))) { throw "Package missing: $file" }
}
$vlc = Get-ChildItem $package -Recurse -Filter libvlc.dll | Select-Object -First 1
if (!$vlc -or !(Test-Path (Join-Path $vlc.DirectoryName 'libvlccore.dll')) -or
    !(Test-Path (Join-Path $vlc.DirectoryName 'plugins'))) { throw 'VLC engine or plugins missing' }

