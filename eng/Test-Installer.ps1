param([Parameter(Mandatory)][string]$SetupPath, [Parameter(Mandatory)][string]$PackagePath)
$ErrorActionPreference = 'Stop'
# GitHub의 새 Windows 러너에서만 실행한다. 로컬 설치 검사에는 사용하지 않는다.
if ($env:GITHUB_ACTIONS -ne 'true') { throw 'Installer verification requires a disposable GitHub runner' }
$setup = (Resolve-Path -LiteralPath $SetupPath).Path
$installer = Start-Process -FilePath $setup -ArgumentList '--silent' -WindowStyle Hidden -PassThru
if (!$installer.WaitForExit(120000)) {
  Stop-Process -Id $installer.Id -Force
  throw 'Installer timed out'
}
if ($installer.ExitCode -ne 0) { throw "Installer failed: $($installer.ExitCode)" }
$installed = Join-Path $env:LOCALAPPDATA 'KOTU/current'
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
& "$PSScriptRoot/Test-AppStartup.ps1" -PackagePath $installed

