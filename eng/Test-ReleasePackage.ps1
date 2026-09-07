param([Parameter(Mandatory)][string]$PackagePath, [Parameter(Mandatory)][string]$Version)
$ErrorActionPreference = 'Stop'
$package = (Resolve-Path -LiteralPath $PackagePath).Path
foreach ($name in @('KOTU-win-Setup.exe', 'KOTU-win-Portable.zip', 'releases.win.json')) {
    $file = Get-Item -LiteralPath (Join-Path $package $name)
    if ($file.PSIsContainer -or $file.Length -eq 0) { throw "Invalid release asset: $name" }
}
$feed = Get-Content -LiteralPath (Join-Path $package 'releases.win.json') -Raw | ConvertFrom-Json
$assets = @($feed.Assets)
if (!($assets | Where-Object { $_.PackageId -eq 'KOTU' -and $_.Version -eq $Version -and $_.Type -eq 'Full' })) {
    throw "Update feed has no full package for $Version"
}
foreach ($asset in $assets) {
    if ($asset.Version -ne $Version) { continue }
    # 피드 파일명을 경로로 해석하기 전에 단일 파일명인지 확인한다.
    if ([IO.Path]::GetFileName($asset.FileName) -ne $asset.FileName) { throw 'Unsafe feed filename' }
    $file = Get-Item -LiteralPath (Join-Path $package $asset.FileName)
    if ($file.Length -ne $asset.Size -or
        (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA1).Hash -ne $asset.SHA1) {
        throw "Update feed hash/size mismatch: $($asset.FileName)"
    }
    if ($asset.SHA256 -and (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne $asset.SHA256) {
        throw "Update feed SHA256 mismatch: $($asset.FileName)"
    }
}
Write-Output "Release assets and update feed verified: $Version"
