[CmdletBinding()]
param(
    [string]$VcToolsPath,
    [string]$WindowsSdkPath,
    [string]$WindowsSdkLibraryPath,
    [string]$WindowsSdkVersion,
    [string]$FrameworkReferencePath,
    [string]$NetFxSdkLibraryPath,
    [string]$OutputPath = (Join-Path $PSScriptRoot '../artifacts/recording-engine'),
    [switch]$PrepareOnly,
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$vendor = Join-Path $repository 'vendor/ScreenRecorderLib'
$manifestPath = Join-Path $vendor 'source-manifest.json'
& (Join-Path $PSScriptRoot 'Test-RecordingEngine.ps1') -CheckSourcesOnly
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Force -Path $OutputPath | Out-Null
# 동시에 실행된 네이티브 빌드가 같은 최종 파일을 덮어쓰지 못하게 한다.
$lock = $null
$savedEnvironment = @{}
try {
    try { $lock = [IO.File]::Open((Join-Path $OutputPath '.build.lock'), 'OpenOrCreate', 'ReadWrite', 'None') }
    catch { throw 'Another recording engine build is using this output directory. Wait for it to finish.' }
    $enginePath = Join-Path $OutputPath 'ScreenRecorderLib.dll'
    if (!$Force -and !$PrepareOnly -and (Test-Path -LiteralPath $enginePath)) {
        try {
            & (Join-Path $PSScriptRoot 'Test-RecordingEngine.ps1') -EnginePath $enginePath
            Write-Output 'Recording engine is current.'
            return
        } catch { Write-Output 'Rebuilding the outdated recording engine.' }
    }
    if (!$VcToolsPath) {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
        if (!(Test-Path -LiteralPath $vswhere)) { throw 'VS 2022 C++ tools were not found. Supply -VcToolsPath, or install the VC x64/x86, C++/CLI and ATL components separately.' }
        $vs = & $vswhere -latest -products '*' -version '[17.0,18.0)' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 Microsoft.VisualStudio.Component.VC.CLI.Support Microsoft.VisualStudio.Component.VC.ATL -property installationPath
        if (!$vs) { throw 'VS 2022 requires VC x64/x86, C++/CLI and ATL components. This script does not install tools.' }
        $vcVersion = (Get-Content -LiteralPath (Join-Path $vs 'VC/Auxiliary/Build/Microsoft.VCToolsVersion.default.txt') -Raw).Trim()
        $VcToolsPath = Join-Path $vs ('VC/Tools/MSVC/' + $vcVersion)
    }
    if (!$WindowsSdkPath) {
        $WindowsSdkPath = $env:WindowsSdkDir
        if (!$WindowsSdkPath) { $WindowsSdkPath = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots' -ErrorAction Stop).KitsRoot10 }
    }
    if (!$WindowsSdkVersion) {
        $WindowsSdkVersion = Get-ChildItem -LiteralPath (Join-Path $WindowsSdkPath 'Include') -Directory |
            Where-Object { $_.Name -match '^10\.0\.\d+\.0$' -and (Test-Path (Join-Path $WindowsSdkPath ('bin/' + $_.Name + '/x64/fxc.exe'))) } |
            Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1 -ExpandProperty Name
        if (!$WindowsSdkVersion) { throw 'A Windows 10/11 SDK with x64 FXC is required.' }
    }
    if (!$WindowsSdkLibraryPath) { $WindowsSdkLibraryPath = Join-Path $WindowsSdkPath ('Lib/' + $WindowsSdkVersion) }
    if (!$FrameworkReferencePath) { $FrameworkReferencePath = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies/Microsoft/Framework/.NETFramework/v4.8' }
    if (!$NetFxSdkLibraryPath) { $NetFxSdkLibraryPath = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/NETFXSDK/4.8/Lib/um/x64' }
    $taskVc = [IO.Path]::GetFullPath($VcToolsPath)
    $taskSdk = [IO.Path]::GetFullPath($WindowsSdkPath)
    $taskSdkLib = [IO.Path]::GetFullPath($WindowsSdkLibraryPath)
    $taskFx = [IO.Path]::GetFullPath($FrameworkReferencePath)
    $taskBin = Join-Path $taskVc 'bin/Hostx64/x64'
    $taskSdkBin = Join-Path $taskSdk ('bin/' + $WindowsSdkVersion + '/x64')
    $required = @(
        (Join-Path $taskBin 'cl.exe'), (Join-Path $taskBin 'link.exe'), (Join-Path $taskBin 'lib.exe'),
        (Join-Path $taskVc 'include/vcclr.h'), (Join-Path $taskVc 'atlmfc/include/atlbase.h'),
        (Join-Path $taskVc 'lib/x64/msvcprt.lib'), (Join-Path $taskVc 'lib/x64/msvcrt.lib'),
        (Join-Path $taskVc 'lib/x64/vcruntime.lib'), (Join-Path $taskVc 'lib/x64/msvcmrt.lib'),
        (Join-Path $taskSdkBin 'fxc.exe'), (Join-Path $taskSdkBin 'rc.exe'),
        (Join-Path $taskSdkLib 'um/x64/kernel32.lib'), (Join-Path $taskSdkLib 'ucrt/x64/ucrt.lib'),
        (Join-Path $NetFxSdkLibraryPath 'mscoree.lib'), (Join-Path $taskFx 'mscorlib.dll')
    )
    foreach ($path in $required) { if (!(Test-Path -LiteralPath $path)) { throw "Missing recording engine prerequisite: $path" } }
    $taskStage = Join-Path $OutputPath ('builds/' + [Guid]::NewGuid().ToString('N'))
    $taskSource = Join-Path $taskStage 'source'
    $taskOut = Join-Path $taskStage 'output'
    $taskRsp = Join-Path $taskStage 'response'
    New-Item -ItemType Directory -Force -Path $taskSource,$taskOut,$taskRsp,(Join-Path $taskStage 'obj/native'),(Join-Path $taskStage 'obj/managed'),(Join-Path $taskStage 'logs') | Out-Null
    foreach ($file in $manifest.Files) {
        $destination = Join-Path $taskSource $file.Path
        New-Item -ItemType Directory -Force -Path (Split-Path $destination -Parent) | Out-Null
        Copy-Item -LiteralPath (Join-Path $vendor $file.Path) -Destination $destination
        if ((Get-FileHash -LiteralPath $destination).Hash -ne $file.SHA256) { throw "Copied engine source changed: $($file.Path)" }
    }
    $taskNative = Join-Path $taskSource 'ScreenRecorderLibNative'
    $taskManaged = Join-Path $taskSource 'ScreenRecorderLib'
    # 인라인 로그의 __FILE__에는 논리 경로를 사용한다. 원본 바이트와 행 번호는 보존한다.
    $sourceNameMetadata = @()
    foreach ($header in @('cleanup.h', 'CMFSinkWriterCallback.h')) {
        $path = Join-Path $taskNative $header
        $originalBytes = [IO.File]::ReadAllBytes($path)
        $prefixText = '#line 1 "ScreenRecorderLibNative\\' + $header + '"' + "`r`n"
        $prefix = [Text.Encoding]::UTF8.GetBytes($prefixText)
        $sourceNameMetadata += [ordered]@{ Path = 'ScreenRecorderLibNative/' + $header; Prefix = $prefixText; OriginalSHA256 = (Get-FileHash -LiteralPath $path).Hash }
        [IO.File]::WriteAllBytes($path, [byte[]]($prefix + $originalBytes))
    }
    $taskIdentity = [Reflection.AssemblyName]::new($manifest.AssemblyIdentity)
    $assembly = Join-Path $taskManaged 'AssemblyInfo.cpp'
    $assemblyText = [IO.File]::ReadAllText($assembly)
    $oldVersion = '[assembly:AssemblyVersionAttribute("1.0.*")];'
    if (!$assemblyText.Contains($oldVersion)) { throw 'Upstream assembly version anchor changed.' }
    [IO.File]::WriteAllText($assembly, $assemblyText.Replace($oldVersion, '[assembly:AssemblyVersionAttribute("' + $taskIdentity.Version + '")];'), [Text.UTF8Encoding]::new($false))
    $taskFramework = Join-Path $taskManaged 'TargetFramework.cpp'
    [IO.File]::WriteAllText($taskFramework, '[assembly:System::Runtime::Versioning::TargetFrameworkAttribute(".NETFramework,Version=v4.8", FrameworkDisplayName=".NET Framework 4.8")];', [Text.UTF8Encoding]::new($false))
    foreach ($name in @('PATH','INCLUDE','LIB','LIBPATH')) { $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
    $env:PATH = $taskBin + ';' + $taskSdkBin + ';' + $env:PATH
    $env:INCLUDE = (@((Join-Path $taskVc 'include'), (Join-Path $taskVc 'atlmfc/include')) + @('ucrt','shared','um','winrt','cppwinrt' | ForEach-Object { Join-Path $taskSdk ('Include/' + $WindowsSdkVersion + '/' + $_) })) -join ';'
    $env:LIB = @((Join-Path $taskVc 'lib/x64'), (Join-Path $taskVc 'atlmfc/lib/x64'), (Join-Path $taskSdkLib 'ucrt/x64'), (Join-Path $taskSdkLib 'um/x64'), $NetFxSdkLibraryPath) -join ';'
    $env:LIBPATH = $taskFx + ';' + (Join-Path $taskVc 'lib/x64')
$taskRecipe=[Collections.Generic.List[object]]::new()
function Quote-Response([string]$Value){'"'+$Value+'"'}
function Add-Response([string]$Name,[string]$Exe,[string[]]$Arguments,[string]$WorkingDirectory){
 $taskFile=Join-Path $taskRsp ($Name+'.rsp')
 [IO.File]::WriteAllLines($taskFile,$Arguments,[Text.UTF8Encoding]::new($false))
 $taskRecipe.Add([ordered]@{Name=$Name;Exe=$Exe;Response=$taskFile;WorkingDirectory=$WorkingDirectory})
}
foreach($taskShader in @(@('PixelShader','PS','ps_4_0_level_9_1'),@('VertexShader','VS','vs_4_0_level_9_1'))){Add-Response ('shader-'+$taskShader[0]) (Join-Path $taskSdkBin 'fxc.exe') @('/nologo','/O3','/T',$taskShader[2],'/E',$taskShader[1],'/Vn',('g_'+$taskShader[1]),'/Fh',(Quote-Response (Join-Path $taskOut ($taskShader[0]+'.h'))),(Quote-Response (Join-Path $taskNative ($taskShader[0]+'.hlsl')))) $taskNative}
$taskNativeFlags=@('/nologo','/c','/O2','/Ob2','/Oi','/Gy','/Zi','/GS','/MD','/EHa','/std:c++17','/utf-8','/W3','/GL-','/sdl-','/DNDEBUG','/DUNICODE','/D_UNICODE','/DSCREENRECORDERLIBNATIVE_EXPORTS','/D_WINDOWS','/D_USRDLL','/D_HAS_STD_BYTE=0','/D_DISABLE_CONSTEXPR_MUTEX_CONSTRUCTOR','/DNOMINMAX',('/I'+(Quote-Response '..\..\output')),('/Fd'+(Quote-Response (Join-Path $taskStage 'obj/native/compiler.pdb'))))
[xml]$taskNativeXml=Get-Content (Join-Path $taskNative 'ScreenRecorderLibNative.vcxproj') -Raw
$taskObjects=@()
foreach($taskNode in $taskNativeXml.Project.ItemGroup.ClCompile){if(-not $taskNode.Include){continue};$taskName=[IO.Path]::GetFileNameWithoutExtension($taskNode.Include);$taskObj=Join-Path $taskStage ('obj/native/'+$taskName+'.obj');$taskObjects+=$taskObj;Add-Response ('native-'+$taskName) (Join-Path $taskBin 'cl.exe') ($taskNativeFlags+@((Quote-Response $taskNode.Include),('/Fo'+(Quote-Response $taskObj)))) $taskNative}
$taskNativeLib=Join-Path $taskOut 'ScreenRecorderLibNative.lib'
Add-Response 'native-library' (Join-Path $taskBin 'lib.exe') (@('/NOLOGO','/MACHINE:X64',('/OUT:'+(Quote-Response $taskNativeLib)))+@($taskObjects | ForEach-Object {Quote-Response $_})) $taskNative
$taskManagedFlags=@('/nologo','/c','/O2','/Ob2','/Zi','/GS','/MD','/EHa','/std:c++17','/utf-8','/W3','/GL-','/clr','/clr:nostdlib','/DNDEBUG','/DUNICODE','/D_UNICODE','/D_HAS_STD_BYTE=0',('/I'+(Quote-Response '..\..\output')),('/I'+(Quote-Response '..\ScreenRecorderLibNative')),('/AI'+(Quote-Response $taskFx)),('/Fd'+(Quote-Response (Join-Path $taskStage 'obj/managed/compiler.pdb'))))
foreach($taskReference in @('mscorlib.dll','System.dll','System.Data.dll','System.Xml.dll')){$taskManagedFlags+=('/FU'+(Quote-Response (Join-Path $taskFx $taskReference)))}
$taskManagedObjects=@()
foreach($taskFile in @((Join-Path $taskManaged 'AssemblyInfo.cpp'),(Join-Path $taskManaged 'Recorder.cpp'),$taskFramework)){$taskName=[IO.Path]::GetFileNameWithoutExtension($taskFile);$taskObj=Join-Path $taskStage ('obj/managed/'+$taskName+'.obj');$taskManagedObjects+=$taskObj;Add-Response ('managed-'+$taskName) (Join-Path $taskBin 'cl.exe') ($taskManagedFlags+@((Quote-Response ([IO.Path]::GetFileName($taskFile))),('/Fo'+(Quote-Response $taskObj)))) $taskManaged}
$taskResource=Join-Path $taskOut 'ScreenRecorderLib.res'
Add-Response 'resource' (Join-Path $taskSdkBin 'rc.exe') @('/nologo','/d','NDEBUG','/d','UNICODE','/d','_UNICODE','/fo',(Quote-Response $taskResource),(Quote-Response (Join-Path $taskManaged 'app.rc'))) $taskManaged
$taskDll=Join-Path $taskOut 'ScreenRecorderLib.dll'
$taskLink=@('/NOLOGO','/DLL','/MACHINE:X64','/SUBSYSTEM:WINDOWS','/OPT:REF','/OPT:ICF','/DEBUG','/INCREMENTAL:NO','/DYNAMICBASE','/NXCOMPAT','/MANIFEST:EMBED','/MANIFESTUAC:NO','/ASSEMBLYDEBUG:DISABLE','/PDBALTPATH:ScreenRecorderLib.pdb',('/OUT:'+(Quote-Response $taskDll)),('/PDB:'+(Quote-Response (Join-Path $taskOut 'ScreenRecorderLib.pdb'))),('/IMPLIB:'+(Quote-Response (Join-Path $taskOut 'ScreenRecorderLib.lib'))))
$taskLink+=@($taskManagedObjects | ForEach-Object {Quote-Response $_})
$taskLink+=@((Quote-Response $taskNativeLib),(Quote-Response $taskResource),'D3D11.lib','dxgi.lib','Mfuuid.lib','Mfplat.lib','evr.lib','mfreadwrite.lib','kernel32.lib','user32.lib','gdi32.lib','winspool.lib','comdlg32.lib','advapi32.lib','shell32.lib','ole32.lib','oleaut32.lib','uuid.lib','odbc32.lib','odbccp32.lib')
Add-Response 'link' (Join-Path $taskBin 'link.exe') $taskLink $taskManaged
$taskRecipe | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $taskStage 'recipe.json') -Encoding utf8
if ($PrepareOnly) { Write-Output ('Prepared ' + $taskRecipe.Count + ' compiler commands: ' + $taskStage); return }
& (Join-Path $PSScriptRoot 'Test-RecordingEngineNative.ps1') -SourcePath $taskSource -OutputPath (Join-Path $taskStage 'tests') -CompilerPath (Join-Path $taskBin 'cl.exe')
$taskSteps = [Collections.Generic.List[object]]::new()
foreach ($command in $taskRecipe) {
    Push-Location -LiteralPath $command.WorkingDirectory
    try {
        if ([IO.Path]::GetFileName($command.Exe) -in @('fxc.exe','rc.exe')) {
            $arguments = @(Get-Content -LiteralPath $command.Response | ForEach-Object { if ($_ -match '^"(.*)"$') { $Matches[1] } else { $_ } })
        } else { $arguments = @('@' + $command.Response) }
        & $command.Exe @arguments *> (Join-Path $taskStage ('logs/' + $command.Name + '.log'))
        $code = $LASTEXITCODE
    } finally { Pop-Location }
    $taskSteps.Add([ordered]@{ Name = $command.Name; ExitCode = $code })
    $taskSteps | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskStage 'steps.json') -Encoding utf8
    if ($code -ne 0) {
        Get-Content (Join-Path $taskStage ('logs/' + $command.Name + '.log')) -Tail 25
        throw ('Recording engine build failed at ' + $command.Name + '. Logs: ' + $taskStage)
    }
}
if ([Reflection.AssemblyName]::GetAssemblyName($taskDll).FullName -ne $manifest.AssemblyIdentity) { throw 'Built recording engine assembly identity changed.' }
$binary = [IO.File]::ReadAllBytes($taskDll)
foreach ($encoding in @([Text.Encoding]::UTF8, [Text.Encoding]::Unicode)) {
    $binaryText = $encoding.GetString($binary)
    foreach ($prefix in @($repository, $taskStage, $repository.Replace('\','/'), $taskStage.Replace('\','/'))) {
        if ($binaryText.IndexOf($prefix, [StringComparison]::OrdinalIgnoreCase) -ge 0) { throw 'Built engine contains a private source/build path.' }
    }
}
$warnings = @(Get-ChildItem (Join-Path $taskStage 'logs') -Filter '*.log' | Select-String -Pattern 'warning [A-Z]+\d+')
$receipt = [ordered]@{
    SourceCommit = $manifest.Commit
    SourceManifestSHA256 = (Get-FileHash -LiteralPath $manifestPath).Hash
    BuildScriptSHA256 = (Get-FileHash -LiteralPath $PSCommandPath).Hash
    TestScriptSHA256 = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'Test-RecordingEngineNative.ps1')).Hash
    TestSourceSHA256 = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'recording-engine-tests/control-flow-tests.cpp')).Hash
    EngineSHA256 = (Get-FileHash -LiteralPath $taskDll).Hash
    AssemblyIdentity = $manifest.AssemblyIdentity
    Framework = $manifest.Framework
    GeneratedSourceNames = $sourceNameMetadata
    CompilerVersion = (Get-Item -LiteralPath (Join-Path $taskBin 'cl.exe')).VersionInfo.FileVersion
    WindowsSdkVersion = $WindowsSdkVersion
    WarningCount = $warnings.Count
    WarningCodes = @($warnings | ForEach-Object { [regex]::Match($_.Line, 'warning ([A-Z]+\d+)').Groups[1].Value } | Sort-Object -Unique)
}
$receipt | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $taskOut 'build-receipt.json') -Encoding utf8
# 완성된 파일만 교체한다. 교체 중인 읽기는 영수증 검증에서 안전하게 실패한다.
foreach ($name in @('ScreenRecorderLib.pdb','ScreenRecorderLib.dll','build-receipt.json')) {
    $temporary = Join-Path $OutputPath ($name + '.new')
    Copy-Item -LiteralPath (Join-Path $taskOut $name) -Destination $temporary -Force
    [IO.File]::Move($temporary, (Join-Path $OutputPath $name), $true)
}
& (Join-Path $PSScriptRoot 'Test-RecordingEngine.ps1') -EnginePath $enginePath
Write-Output ('Built patched recording engine: ' + $enginePath + '; warnings: ' + $warnings.Count + '; logs: ' + $taskStage)
} finally {
    foreach ($name in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process') }
    if ($lock) { $lock.Dispose() }
}
