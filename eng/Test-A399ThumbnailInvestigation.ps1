# A399-c 이전 정책의 기준선 조사 도구. 새 정책 검증은 docs/A399-CONDITIONAL-RETRY.md 참조.
# 제품 모양이 바뀌면 아래 추출 검사를 실패시킨다. 새 정책에 맞춰 옛 기준선을 변형하지 않는다.
param([int]$Rounds = 3)
$ErrorActionPreference = 'Stop'
if ($Rounds -lt 1 -or $Rounds -gt 10) { throw 'Rounds must be between 1 and 10.' }
$repo = Split-Path $PSScriptRoot -Parent
$tileSource = Get-Content -Raw -LiteralPath (Join-Path $repo 'src/KOTU.App/Controls/ThumbnailExplorer.xaml.cs')
$watchSource = Get-Content -Raw -LiteralPath (Join-Path $repo 'src/KOTU.Core/Threading/FolderWatch.cs')
# 제품의 현재 메서드/필터를 추출한다. UI 전체를 실행하는 시험은 아니다.
$miss = [regex]::Match($tileSource, '(?ms)^    private static void MarkPreviewMiss\(ExplorerEntryVm vm, bool timedOut\).*?^    }')
$filter = [regex]::Match($watchSource, 'Filters = (NotifyFilters\.FileName[^;]+);')
if (!$miss.Success -or !$filter.Success) { throw 'Source shape changed; review the investigation probe.' }
$method = $miss.Value.Replace('private static', 'public static')
$filters = $filter.Groups[1].Value
$probeSource = @"
using System;
using System.IO;
using System.Threading;
namespace A399Investigation {
public sealed class ExplorerEntryVm {
    public bool PreviewKnownEmpty { get; set; }
    public bool PreviewTimedOut { get; set; }
}
public static class Probe {
$method
    public static NotifyFilters CurrentFilter => $filters;
    public static object Watch(string root, int rounds) {
        var path = Path.Combine(root, "synthetic.txt");
        File.WriteAllText(path, "a399-local-only");
        int current = 0, attributes = 0, errors = 0;
        var observations = new System.Collections.Generic.List<object>();
        try {
            using (var a = new FileSystemWatcher(root, "synthetic.txt"))
            using (var b = new FileSystemWatcher(root, "synthetic.txt")) {
                a.NotifyFilter = CurrentFilter;
                b.NotifyFilter = CurrentFilter | NotifyFilters.Attributes;
                a.Changed += (_, e) => Interlocked.Increment(ref current);
                b.Changed += (_, e) => Interlocked.Increment(ref attributes);
                a.Error += (_, e) => Interlocked.Increment(ref errors);
                b.Error += (_, e) => Interlocked.Increment(ref errors);
                a.EnableRaisingEvents = b.EnableRaisingEvents = true;
                Thread.Sleep(150);
                for (int i = 0; i < rounds; i++) {
                    int beforeA = Volatile.Read(ref current), beforeB = Volatile.Read(ref attributes);
                    var before = new FileInfo(path);
                    var length = before.Length;
                    var modified = before.LastWriteTimeUtc;
                    File.SetAttributes(path, File.GetAttributes(path) ^ FileAttributes.Hidden);
                    Thread.Sleep(1000);
                    var after = new FileInfo(path);
                    observations.Add(new {
                        Round = i + 1,
                        CurrentEvents = Volatile.Read(ref current) - beforeA,
                        WithAttributesEvents = Volatile.Read(ref attributes) - beforeB,
                        LengthUnchanged = after.Length == length,
                        ModifiedUnchanged = after.LastWriteTimeUtc == modified
                    });
                }
                int controlA = Volatile.Read(ref current), controlB = Volatile.Read(ref attributes);
                File.AppendAllText(path, "-content-control");
                Thread.Sleep(1000);
                return new {
                    Filter = CurrentFilter.ToString(),
                    AttributeOnly = observations,
                    ContentControlCurrentEvents = Volatile.Read(ref current) - controlA,
                    ContentControlWithAttributesEvents = Volatile.Read(ref attributes) - controlB,
                    WatcherErrors = Volatile.Read(ref errors)
                };
            }
        } finally {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
    }
}
}
"@
Add-Type -TypeDefinition $probeSource
$ordinary = [A399Investigation.ExplorerEntryVm]::new()
[A399Investigation.Probe]::MarkPreviewMiss($ordinary, $false)
$timeout = [A399Investigation.ExplorerEntryVm]::new()
[A399Investigation.Probe]::MarkPreviewMiss($timeout, $true)
$firstTimeoutEmpty = $timeout.PreviewKnownEmpty
[A399Investigation.Probe]::MarkPreviewMiss($timeout, $true)
$fresh = [A399Investigation.ExplorerEntryVm]::new()
$states = [ordered]@{
    OrdinaryMissKnownEmpty = $ordinary.PreviewKnownEmpty
    FirstTimeoutKnownEmpty = $firstTimeoutEmpty
    SecondTimeoutKnownEmpty = $timeout.PreviewKnownEmpty
    FreshVmKnownEmpty = $fresh.PreviewKnownEmpty
}
if (!$states.OrdinaryMissKnownEmpty -or $states.FirstTimeoutKnownEmpty -or
    !$states.SecondTimeoutKnownEmpty -or $states.FreshVmKnownEmpty) { throw 'Unexpected miss policy.' }
# 사용자의 동기화 파일은 열거나 상태를 바꾸지 않는다. 임시 디렉터리만 사용한다.
$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
foreach ($cloudRoot in @($env:OneDrive, $env:OneDriveCommercial, $env:OneDriveConsumer)) {
    if ($cloudRoot -and $tempBase.StartsWith([IO.Path]::GetFullPath($cloudRoot).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Temporary directory is under a OneDrive root; no probe was run.'
    }
}
$tempFolder = Join-Path $tempBase ('kotu-a399-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($tempFolder) | Out-Null
try {
    $watch = [A399Investigation.Probe]::Watch($tempFolder, $Rounds)
    [ordered]@{ MissPolicy = $states; Watcher = $watch; Scope = 'Synthetic local file; no OneDrive provider or WinUI execution' } |
        ConvertTo-Json -Depth 8
    if ($watch.WatcherErrors -ne 0 -or $watch.ContentControlCurrentEvents -lt 1 -or
        $watch.ContentControlWithAttributesEvents -lt 1) { throw 'Watcher control failed.' }
    foreach ($round in $watch.AttributeOnly) {
        if ($round.CurrentEvents -lt 1 -or $round.WithAttributesEvents -lt 1 -or
            !$round.LengthUnchanged -or !$round.ModifiedUnchanged) { throw 'Attribute-only observation differed.' }
    }
} finally {
    # 재귀 삭제를 쓰지 않는다. 고유 임시 폴더에 남은 파일이 있으면 실패시켜 보존한다.
    [IO.Directory]::Delete($tempFolder, $false)
}
