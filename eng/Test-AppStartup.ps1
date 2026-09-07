param([Parameter(Mandatory)][string]$PackagePath)
$ErrorActionPreference = 'Stop'
$package = (Resolve-Path -LiteralPath $PackagePath).Path
$app = Start-Process -FilePath (Join-Path $package 'KOTU.exe') -WorkingDirectory $package -WindowStyle Hidden -PassThru
try {
  Start-Sleep -Seconds 10
  $app.Refresh()
  if ($app.HasExited) { throw "KOTU exited during startup: $($app.ExitCode)" }
  $startupLog = Join-Path $env:TEMP 'KOTU/startup-error.log'
  if (Test-Path $startupLog) { throw (Get-Content $startupLog -Raw) }
} finally {
  if (!$app.HasExited) { Stop-Process -Id $app.Id -Force }
}

