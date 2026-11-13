[CmdletBinding()]
param(
    [string]$SourcePath = (Join-Path $PSScriptRoot '../vendor/ScreenRecorderLib'),
    [Parameter(Mandatory)][string]$OutputPath,
    [string]$CompilerPath = 'cl.exe'
)
$ErrorActionPreference = 'Stop'
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Force -Path $OutputPath | Out-Null
$manager = [IO.File]::ReadAllText((Join-Path $SourcePath 'ScreenRecorderLibNative/RecordingManager.cpp')).Replace("`r`n", "`n")
$wgc = [IO.File]::ReadAllText((Join-Path $SourcePath 'ScreenRecorderLibNative/WindowsGraphicsCapture.cpp')).Replace("`r`n", "`n")
function Require-Source([bool]$Condition, [string]$Message) { if (!$Condition) { throw "Native regression extraction failed: $Message" } }
function Write-Include([string]$Name, [string]$Content) { [IO.File]::WriteAllText((Join-Path $OutputPath $Name), $Content, [Text.UTF8Encoding]::new($false)) }
# 테스트는 제품 함수 본문을 그대로 추출한다. 운영체제와 시간만 시험 대역으로 바꾼다.
$start = $manager.IndexOf('static bool RequiresInitialWindowFrame(')
$end = $manager.IndexOf('REC_RESULT RecordingManager::StartRecorderLoop(')
Require-Source ($start -ge 0 -and $end -gt $start) 'initial gate function boundaries'
$functions = $manager.Substring($start, $end - $start)
Write-Include 'actual-gate.inc' $functions
$getNext = $wgc.IndexOf('HRESULT WindowsGraphicsCapture::GetNextFrame')
$timeoutBranch = $wgc.IndexOf("`n`telse if (result == WAIT_TIMEOUT)", $getNext)
Require-Source ($getNext -ge 0 -and $timeoutBranch -gt $getNext) 'WGC frame function boundaries'
$emptyStart = $wgc.LastIndexOf("`n`t`telse {`n", $timeoutBranch)
$emptyEnd = $wgc.IndexOf("`n`t`t}", $emptyStart)
Require-Source ($emptyStart -gt $getNext -and $emptyEnd -gt $emptyStart) 'empty pool branch'
$emptyStart += "`n`t`telse {`n".Length
$emptyBranch = $wgc.Substring($emptyStart, $emptyEnd - $emptyStart)
Write-Include 'actual-empty-pool.inc' $emptyBranch
# 수정 전 동작을 재현하는 고정 회귀 입력이며 제품 구현의 대체본이 아니다.
Write-Include 'prior-empty-pool.inc' 'hr = DXGI_ERROR_WAIT_TIMEOUT;'
$guard = [regex]::Match($wgc, 'if \(!m_HasWrittenNativeFrame\) return DXGI_ERROR_WAIT_TIMEOUT;')
$mark = [regex]::Match($wgc, 'm_HasWrittenNativeFrame = true;')
Require-Source ($guard.Success -and $mark.Success) 'native write and placeholder statements'
Write-Include 'actual-placeholder-guard.inc' $guard.Value
Write-Include 'actual-native-copy-mark.inc' $mark.Value
$gate = $manager.IndexOf('const auto initial = WaitForInitialWindowFrame(', $end)
$audio = $manager.IndexOf('m_AudioManager->StartCapture();', $end)
$sink = $manager.IndexOf('m_OutputManager->BeginRecording(', $end)
Require-Source ($gate -gt $end -and $audio -gt $gate -and $sink -gt $gate) 'gate must precede audio and output clock'
Require-Source ($functions -notmatch 'Pixel|BeginRecording|StartMediaClock|OnVideoFrame') 'readiness cannot depend on pixels or start output'
Require-Source (([regex]::Matches($wgc, 'm_HasWrittenNativeFrame\(false\)')).Count -eq 1) 'one constructor initialization'
Require-Source (([regex]::Matches($wgc, 'm_HasWrittenNativeFrame = true')).Count -eq 1) 'one actual native write mark'
Require-Source ($mark.Index -gt $wgc.IndexOf('m_DeviceContext->CopySubresourceRegion(pSharedSurf')) 'write mark must follow the shared-surface copy'
$testSource = Join-Path $OutputPath 'control-flow-tests.cpp'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'recording-engine-tests/control-flow-tests.cpp') -Destination $testSource
$executable = Join-Path $OutputPath 'control-flow-tests.exe'
$arguments = @('/nologo','/std:c++17','/EHsc','/MD','/W4','/WX','/utf-8', 'control-flow-tests.cpp', ('/Fo' + (Join-Path $OutputPath 'control-flow-tests.obj')), ('/Fe' + $executable))
Push-Location -LiteralPath $OutputPath
try {
    & $CompilerPath @arguments *> (Join-Path $OutputPath 'compile.log')
    if ($LASTEXITCODE -ne 0) { Get-Content (Join-Path $OutputPath 'compile.log'); throw 'Native regression harness compilation failed.' }
    $startInfo = [Diagnostics.ProcessStartInfo]::new($executable)
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($startInfo)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(30000)) { $process.Kill($true); $process.WaitForExit(); throw 'Native recording regression timed out after 30 seconds.' }
        [IO.File]::WriteAllText((Join-Path $OutputPath 'run.log'), $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult())
        if ($process.ExitCode -ne 0) { Get-Content (Join-Path $OutputPath 'run.log'); throw 'Native recording regression failed.' }
    } finally { $process.Dispose() }
    Get-Content (Join-Path $OutputPath 'run.log')
} finally { Pop-Location }
