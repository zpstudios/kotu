param(
    [Parameter(Mandatory)][string]$FixtureRoot,
    [ValidateSet('Hide', 'Show', 'Burst', 'Sustained')][string]$Mode = 'Burst'
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($FixtureRoot).TrimEnd('\')
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
if (!$root.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetFileName($root) -ne 'kotu-a399-a-ui') { throw 'Use only the dedicated local temporary fixture.' }
foreach ($cloud in @($env:OneDrive, $env:OneDriveCommercial, $env:OneDriveConsumer)) {
    if ($cloud -and $root.StartsWith([IO.Path]::GetFullPath($cloud).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Cloud fixtures are not allowed.'
    }
}
$path = Join-Path $root 'rename.txt'
$trace = Join-Path ([IO.Path]::GetTempPath()) 'KOTU/trace.log'
$beforeLines = @(Get-Content -LiteralPath $trace).Count
$before = Get-Item -LiteralPath $path -Force
$length = $before.Length
$modified = $before.LastWriteTimeUtc
$hash = (Get-FileHash -LiteralPath $path).Hash
$attributes = [IO.File]::GetAttributes($path)
$count = 1
if ($Mode -eq 'Hide') { [IO.File]::SetAttributes($path, $attributes -bor [IO.FileAttributes]::Hidden) }
elseif ($Mode -eq 'Show') { [IO.File]::SetAttributes($path, $attributes -band (-bnot [IO.FileAttributes]::Hidden)) }
else {
    $count = 100
    try {
        for ($i = 0; $i -lt $count; $i++) {
            [IO.File]::SetAttributes($path, [IO.File]::GetAttributes($path) -bxor [IO.FileAttributes]::ReadOnly)
            Start-Sleep -Milliseconds $(if ($Mode -eq 'Burst') { 10 } else { 450 })
        }
    } finally { [IO.File]::SetAttributes($path, $attributes) }
}
Start-Sleep -Milliseconds 1500
$after = Get-Item -LiteralPath $path -Force
$lines = @(Get-Content -LiteralPath $trace | Select-Object -Skip $beforeLines | Where-Object { $_.Contains($root) })
[ordered]@{
    Mode = $Mode
    AttributeWrites = $count
    ScansStarted = @($lines | Where-Object { $_.Contains('nav | NavigateToAsync') }).Count
    ScansCompleted = @($lines | Where-Object { $_.Contains('nav | scan done') }).Count
    TileListsApplied = @($lines | Where-Object { $_.Contains('tiles | ShowEntries') }).Count
    ShellPreviewRequests = @($lines | Where-Object { $_.Contains('tiles | fill shell') }).Count
    TextPreviewRequests = @($lines | Where-Object { $_.Contains('tiles | fill text') }).Count
    TextPreviewsApplied = @($lines | Where-Object { $_.Contains('tiles | text done') }).Count
    ContentUnchanged = (Get-FileHash -LiteralPath $path).Hash -eq $hash
    LengthUnchanged = $after.Length -eq $length
    ModifiedUnchanged = $after.LastWriteTimeUtc -eq $modified
    Scope = 'Local synthetic fixture and opt-in trace; not a OneDrive provider test.'
} | ConvertTo-Json
