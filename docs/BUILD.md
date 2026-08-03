# 빌드 가이드

소스에서 WinUtil을 빌드해 실행하기까지의 전체 과정입니다.

## 1. 필요 환경

| 항목 | 요구 사항 |
|---|---|
| OS | Windows 10 버전 1809(17763) 이상, x64 |
| SDK | [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) |
| IDE | Visual Studio 2022 17.8+ — 워크로드 **".NET 데스크톱 개발"** + 개별 구성요소 **"Windows App SDK"** (또는 워크로드 "WinUI 애플리케이션 개발") |
| 기타 | 압축 기능 사용 시 7-Zip의 `7z.dll` (아래 3장) |

Visual Studio 없이 CLI만으로도 빌드됩니다 (.NET 8 SDK + Windows SDK 빌드 도구는 NuGet으로 자동 복원).

## 2. 빌드

```powershell
git clone https://github.com/tsusaikang/winutil.git
cd winutil

# 단위 테스트 (Core 라우터·설정, 모듈 순수 로직)
dotnet test

# 디버그 빌드
dotnet build src/WinUtil.App -p:Platform=x64

# 실행
.\src\WinUtil.App\bin\x64\Debug\net8.0-windows10.0.19041.0\WinUtil.App.exe
```

Visual Studio에서는 `WinUtil.sln`을 열고 시작 프로젝트를 `WinUtil.App`, 플랫폼을 `x64`로 두고 F5.

앱은 **unpackaged**(MSIX 아님)로 실행되며, `WindowsAppSDKSelfContained=true`라 Windows App SDK 런타임을 따로 설치할 필요가 없습니다.

## 3. 7z.dll 준비 (압축 모듈)

압축 모듈은 LGPL인 7-Zip의 `7z.dll`(x64)을 실행 시점에 동적 로드합니다. 저장소에는 포함되어 있지 않으므로 한 번만 준비하면 됩니다. 두 방법 중 택일:

1. 실행 폴더에 복사: `copy "C:\Program Files\7-Zip\7z.dll" <빌드 출력 폴더>\`
2. `src/WinUtil.Module.Archive/runtimes/win-x64/7z.dll` 위치에 두기 — 이후 모든 빌드에서 자동 복사됨 (추천)

7-Zip이 설치돼 있지 않다면 [7-zip.org](https://www.7-zip.org/)에서 받으세요. `7z.dll` 없이도 앱은 실행되며, 압축 파일을 열 때 안내 메시지가 표시됩니다.

> **동영상 모듈(libvlc)은 준비가 필요 없습니다.** libvlc 네이티브 바이너리는 NuGet 패키지 `VideoLAN.LibVLC.Windows`로 자동 복원·복사됩니다 (배포 용량 약 +80MB, LGPL·동적 링크).

## 4. 릴리스 빌드

```powershell
dotnet publish src/WinUtil.App -c Release -p:Platform=x64 -r win-x64 --self-contained
```

산출물: `src/WinUtil.App/bin/x64/Release/net8.0-windows10.0.19041.0/win-x64/publish/`
이 폴더에 `7z.dll`을 넣으면 그대로 배포 가능한 포터블 구성이 됩니다.

## 5. 문제 해결

- **`Microsoft.WindowsAppSDK` 복원 실패** — nuget.org 소스가 활성화돼 있는지 확인: `dotnet nuget list source`
- **XAML 컴파일 오류 (WMC/XLS 계열)** — Visual Studio의 "Windows App SDK" 구성요소 미설치가 흔한 원인. CLI 빌드라면 `dotnet workload restore` 시도.
- **앱 실행 직후 종료** — unpackaged 실행에는 x64 빌드가 필수입니다. `-p:Platform=x64` 누락 여부 확인.
- **압축 파일 열기 실패: "7z.dll을 찾을 수 없습니다"** — 3장 참고. 반드시 **x64** dll이어야 합니다(32비트 7-Zip의 dll 불가).
- **동영상 재생 실패 / 검은 화면** — 출력 폴더에 `libvlc.dll`·`libvlccore.dll`과 `plugins\` 폴더가 있는지 확인(NuGet 복원이 정상이면 자동 포함). LibVLCSharp.WinUI는 WindowsAppSDK 1.8+ 필요.
- **테스트 프로젝트 빌드 실패** — 모듈 테스트는 `net8.0-windows` TFM이라 Windows에서만 빌드·실행됩니다.

## 6. 알려진 미검증 지점

현재 코드는 Linux 환경에서 작성되어 Windows 실빌드 검증 전입니다. 첫 빌드에서 보고된 확인 필요 지점은 커밋 메시지와 [ARCHITECTURE.md](../ARCHITECTURE.md) 6장(리스크)을 참고하세요. 빌드 오류를 발견하면 이슈로 남겨주시면 감사하겠습니다.
