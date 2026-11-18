param(
  [Parameter(Mandatory)][string]$SetupPath,
  [Parameter(Mandatory)][string]$PackagePath,
  [ValidateSet('KOTU', 'KOTU.Test')][string]$PackageId = 'KOTU',
  [string]$StableSetupPath
)
$ErrorActionPreference = 'Stop'
# GitHub의 새 Windows 러너에서만 실행한다. 로컬 설치 검사에는 사용하지 않는다.
if ($env:GITHUB_ACTIONS -ne 'true') { throw 'Installer verification requires a disposable GitHub runner' }
function Invoke-Setup([string]$Path, [string[]]$Arguments = @('--silent')) {
  $installer = Start-Process -FilePath (Resolve-Path -LiteralPath $Path).Path -ArgumentList $Arguments -WindowStyle Hidden -PassThru
  try {
    if (!$installer.WaitForExit(120000)) {
      Stop-Process -Id $installer.Id -Force
      throw "Timed out: $Path"
    }
    if ($installer.ExitCode -ne 0) { throw "Failed ($($installer.ExitCode)): $Path" }
  } finally { $installer.Dispose() }
}

. (Join-Path $PSScriptRoot 'StandaloneSnapshots.ps1')
$uninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall'
function Get-InstalledEntry([string]$Title) {
  Get-ChildItem -LiteralPath "HKCU:\$uninstallKey" | Where-Object { $_.GetValue('DisplayName') -ceq $Title }
}
function Assert-Registration([string]$Title, [string]$Root) {
  $entries = @(Get-InstalledEntry $Title)
  if ($entries.Count -ne 1 -or $entries[0].GetValue('InstallLocation') -ne $Root) {
    throw "Installed app entry for $Title does not identify $Root"
  }
  $link = Join-Path ([Environment]::GetFolderPath('Programs')) "$Title.lnk"
  $shell = New-Object -ComObject WScript.Shell
  $target = $shell.CreateShortcut((Resolve-Path -LiteralPath $link).Path).TargetPath
  if (!(Test-Path -LiteralPath $target -PathType Leaf) -or
      !$target.StartsWith($Root + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw "Start menu shortcut for $Title points outside its installation: $target"
  }
}

function Assert-StableState([string[]]$Before) {
  if (($Before -join "`n") -cne (@(Get-StableState | Sort-Object) -join "`n")) {
    throw 'Test package changed stable KOTU files or registration'
  }
}

function Get-StableState {
  Get-AppDataSnapshot
  Get-RegistrySnapshot
  foreach ($key in Get-InstalledEntry 'KOTU') {
    Get-StandaloneRegistryKeySnapshot "$uninstallKey\$($key.PSChildName)" -Recurse
  }
  foreach ($location in 'DesktopDirectory', 'Programs') {
    $link = Join-Path ([Environment]::GetFolderPath($location)) 'KOTU.lnk'
    if (Test-Path -LiteralPath $link) { "shortcut:${link}:$((Get-FileHash -LiteralPath $link).Hash)" }
  }
}

$stable = Join-Path $env:LOCALAPPDATA 'KOTU/current'
if ($StableSetupPath) {
  if ($PackageId -ne 'KOTU.Test') { throw 'A stable baseline is only used for test installations' }
  Invoke-Setup $StableSetupPath
  if (([xml](Get-Content -LiteralPath "$stable/sq.version" -Raw)).package.metadata.id -cne 'KOTU') {
    throw 'The coexistence baseline is not the official KOTU installation'
  }
  & "$PSScriptRoot/Test-AppStartup.ps1" -PackagePath $stable
}
if ($PackageId -eq 'KOTU.Test') { $before = @(Get-StableState | Sort-Object) }

Invoke-Setup $SetupPath
$root = Join-Path $env:LOCALAPPDATA $PackageId
$installed = Join-Path $root 'current'
$manifest = ([xml](Get-Content -LiteralPath "$installed/sq.version" -Raw)).package.metadata
$title = if ($PackageId -eq 'KOTU.Test') { 'KOTU Test' } else { 'KOTU' }
if ($manifest.id -cne $PackageId -or $manifest.title -cne $title) { throw "Unexpected installed identity for $PackageId" }
Assert-Registration $title $root
# Load from bytes so the verifier itself never locks the DLL during uninstall.
$core = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes("$installed/KOTU.Core.dll"))
$policy = $core.GetType('KOTU.Core.Integration.DistributionPolicy', $true)
if ($policy::ProductId -cne $PackageId -or $policy::InstanceKey -cne "$PackageId-Main" -or
    $policy::IsStableDistribution -ne ($PackageId -eq 'KOTU')) { throw "Wrong application flavor in $PackageId Setup" }
$package = (Resolve-Path -LiteralPath $PackagePath).Path
foreach ($file in (Get-ChildItem $package -Recurse -File)) {
  # Velopack 1.2.0 PackageBuilder.REGEX_EXCLUDES + PackCommand.Exclude 기본값.
  # 진단 파일만 제외하며 실행 파일·DLL·리소스의 누락/변경은 계속 실패시킨다.
  if ($file.FullName -imatch '.*[\\/]createdump.*|.*\.vshost\..*|.*\.nupkg$' -or
      $file.FullName -cmatch '.*\.pdb') { continue }
  $relative = [IO.Path]::GetRelativePath($package, $file.FullName)
  $target = Join-Path $installed $relative
  if (!(Test-Path $target)) { throw "Installed file missing: $relative" }
  if ((Get-FileHash $file.FullName).Hash -ne (Get-FileHash $target).Hash) {
    throw "Installed file differs: $relative"
  }
}
& "$PSScriptRoot/Test-AppStartup.ps1" -PackagePath $installed -PackageId $PackageId
if ($StableSetupPath) {
  Assert-StableState $before
  Assert-Registration 'KOTU' (Split-Path -Parent $stable)
  $app = Start-Process -FilePath "$stable/KOTU.exe" -WorkingDirectory $stable -WindowStyle Hidden -PassThru
  try {
    if (!$app.WaitForInputIdle(30000)) { throw 'Stable KOTU did not initialize' }
    & "$PSScriptRoot/Test-AppStartup.ps1" -PackagePath $installed -PackageId $PackageId
    if ($app.HasExited) { throw 'Stable KOTU exited during side-by-side launch' }
  } finally {
    if (!$app.HasExited) {
      Stop-Process -Id $app.Id -Force
      if (!$app.WaitForExit(10000)) { throw 'Stable KOTU remained running after coexistence check' }
    }
    $app.Dispose()
  }
  # Stable ran intentionally for the coexistence check. Measure uninstall from its idle state.
  $before = @(Get-StableState | Sort-Object)
}
if ($PackageId -eq 'KOTU.Test') {
  Invoke-Setup (Join-Path $root 'Update.exe') @('--uninstall', '--silent')
  $testLink = Join-Path ([Environment]::GetFolderPath('Programs')) "$title.lnk"
  if ((Test-Path -LiteralPath "$installed/KOTU.exe") -or (Test-Path -LiteralPath $testLink) -or
      @(Get-InstalledEntry $title).Count) { throw 'Test app remained installed or registered' }
  Assert-StableState $before
}

