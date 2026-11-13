[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
# 실제 제품 파일을 바꾸지 않고 누락·오염·오래된 빌드의 차단 동작을 검사한다.
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('kotu-engine-contract-' + [Guid]::NewGuid().ToString('N'))
$fixtureEng = Join-Path $fixture 'eng'
$fixtureVendor = Join-Path $fixture 'vendor/ScreenRecorderLib'
$fixtureOutput = Join-Path $fixture 'output'
$fixturePublish = Join-Path $fixture 'publish'
New-Item -ItemType Directory -Path $fixtureEng,$fixtureVendor,$fixtureOutput,$fixturePublish,(Join-Path $fixturePublish 'licenses'),(Join-Path $fixtureEng 'recording-engine-tests') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Test-RecordingEngine.ps1') -Destination $fixtureEng
$validator = Join-Path $fixtureEng 'Test-RecordingEngine.ps1'
$procedure = Join-Path $fixtureEng 'Build-RecordingEngine.ps1'
$nativeTest = Join-Path $fixtureEng 'Test-RecordingEngineNative.ps1'
$nativeTestSource = Join-Path $fixtureEng 'recording-engine-tests/control-flow-tests.cpp'
$license = Join-Path $fixtureVendor 'LICENSE'
$manifestPath = Join-Path $fixtureVendor 'source-manifest.json'
$engine = Join-Path $fixtureOutput 'ScreenRecorderLib.dll'
$receiptPath = Join-Path $fixtureOutput 'build-receipt.json'
$published = Join-Path $fixturePublish 'ScreenRecorderLib.dll'
$publishedLicense = Join-Path $fixturePublish 'licenses/ScreenRecorderLib-LICENSE.txt'
# 메타데이터 검사만 하므로 이미 설치된 관리 어셈블리를 안전한 시험 입력으로 사용한다.
$knownAssembly = [System.Linq.Enumerable].Assembly.Location
$knownIdentity = [Reflection.AssemblyName]::GetAssemblyName($knownAssembly).FullName
$passed = 0
function Reset-Fixture {
    Set-Content -LiteralPath $procedure -Value '# fixture build procedure' -Encoding utf8
    Set-Content -LiteralPath $nativeTest -Value '# fixture test procedure' -Encoding utf8
    Set-Content -LiteralPath $nativeTestSource -Value '// fixture test source' -Encoding utf8
    Set-Content -LiteralPath $license -Value 'fixture license' -Encoding utf8
    $manifest = @{ AssemblyIdentity = $knownIdentity; Files = @(@{ Path = 'LICENSE'; SHA256 = (Get-FileHash $license).Hash }) }
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding utf8
    Copy-Item -LiteralPath $knownAssembly -Destination $engine -Force
    @{ SourceManifestSHA256 = (Get-FileHash $manifestPath).Hash; BuildScriptSHA256 = (Get-FileHash $procedure).Hash; TestScriptSHA256 = (Get-FileHash $nativeTest).Hash; TestSourceSHA256 = (Get-FileHash $nativeTestSource).Hash; EngineSHA256 = (Get-FileHash $engine).Hash } |
        ConvertTo-Json | Set-Content -LiteralPath $receiptPath -Encoding utf8
    Copy-Item -LiteralPath $engine -Destination $published -Force
    Copy-Item -LiteralPath $license -Destination $publishedLicense -Force
}
function Expect-Rejected([string]$Name, [scriptblock]$Mutation, [string]$Expected, [switch]$Publish) {
    Reset-Fixture
    & $Mutation
    $failure = $null
    try {
        if ($Publish) { & $validator -EnginePath $engine -PublishedPath $published | Out-Null }
        else { & $validator -EnginePath $engine | Out-Null }
    } catch { $failure = $_.Exception.Message }
    if (!$failure -or $failure -notlike ('*' + $Expected + '*')) { throw "Contract failed: $Name; observed: $failure" }
    $script:passed++
    Write-Output "PASS: $Name"
}
try {
    Reset-Fixture
    & $validator -EnginePath $engine -PublishedPath $published | Out-Null
    $passed++
    Expect-Rejected 'missing engine' { Remove-Item -LiteralPath $engine } 'engine is missing'
    Expect-Rejected 'missing receipt' { Remove-Item -LiteralPath $receiptPath } 'receipt is missing'
    Expect-Rejected 'changed source bytes' { Add-Content -LiteralPath $license -Value 'changed' } 'source differs'
    Expect-Rejected 'updated sources require rebuild' {
        Add-Content -LiteralPath $license -Value 'new revision'
        $data = Get-Content $manifestPath -Raw | ConvertFrom-Json
        $data.Files[0].SHA256 = (Get-FileHash $license).Hash
        $data | ConvertTo-Json -Depth 5 | Set-Content $manifestPath -Encoding utf8
    } 'sources or build procedure changed'
    Expect-Rejected 'updated build procedure requires rebuild' { Add-Content -LiteralPath $procedure -Value '# new flags' } 'sources or build procedure changed'
    Expect-Rejected 'updated native regression requires retest' { Add-Content -LiteralPath $nativeTestSource -Value '// new check' } 'sources or build procedure changed'
    Expect-Rejected 'changed engine bytes' { [IO.File]::AppendAllText($engine, 'changed') } 'does not match'
    Expect-Rejected 'wrong assembly identity despite matching hash' {
        $data = Get-Content $manifestPath -Raw | ConvertFrom-Json
        $data.AssemblyIdentity = 'DifferentAssembly, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null'
        $data | ConvertTo-Json -Depth 5 | Set-Content $manifestPath -Encoding utf8
        $receipt = Get-Content $receiptPath -Raw | ConvertFrom-Json
        $receipt.SourceManifestSHA256 = (Get-FileHash $manifestPath).Hash
        $receipt | ConvertTo-Json | Set-Content $receiptPath -Encoding utf8
    } 'does not match'
    Expect-Rejected 'published engine replaced' { [IO.File]::AppendAllText($published, 'other package') } 'Published recording engine differs' -Publish
    Expect-Rejected 'published license missing' { Remove-Item -LiteralPath $publishedLicense } 'license is missing' -Publish
    Write-Output "Recording engine contract: $passed checks passed."
} finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (!$resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notlike 'kotu-engine-contract-*') { throw 'Unsafe fixture cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
