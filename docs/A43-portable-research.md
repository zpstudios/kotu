# A43 조사 — v0.209.0 시점, 단일 원본

> [A43] 진짜 포터블 단일 파일 실행본(설치본과 별개 배포물) 조사 결과.
> 요구 3가지: ① exe 하나로 압축 해제 없이 바로 실행 ② 자동 업데이트 완전 비활성
> ③ 완전 스탠드얼론(%AppData%·레지스트리 흔적 0).
> 조사 전용 배치 산출물 — 코드 변경 없음. 판정 근거는 실코드(파일:라인)와 웹 출처(URL) 병기.

---

## 1. 현행 빌드·배포 구조

### 1.1 빌드·패키징 파이프라인 (release.yml)

| 단계 | 내용 | 근거 |
|---|---|---|
| publish | `dotnet publish src/KOTU.App -c Release -p:Platform=x64 -r win-x64 --self-contained` | `.github/workflows/release.yml:45` |
| 7z.dll 동봉 | 러너의 7-Zip에서 **publish 후** 복사(LGPL 동적 로드) | `release.yml:47–55` |
| resources.pri 검증 | 없으면 릴리스 중단(실행 불가 zip 방지) | `release.yml:57–79` |
| Velopack 패키징 | `vpk pack --packId KOTU --mainExe KOTU.exe ...` → Setup.exe + Portable.zip + full/delta nupkg + releases.win.json | `release.yml:109–134` |
| 릴리스 | 태그 생성 + `vpk_out/*` 전체 업로드 | `release.yml:136–142` |

핵심 판정: **현행 `KOTU-win-Portable.zip`은 "수동 zip"이 아니라 Velopack이 생성하는 자기
업데이트 포터블 패키지다.** Velopack 문서가 portable 패키지를 "does not need to be installed
and is self-updating"으로 명시하고, 릴리스 본문도 "무설치판 … 역시 자동 업데이트"라고 안내한다
(`release.yml:148`). 출처: https://docs.velopack.io/packaging/overview
→ 요구 ②(업데이트 완전 비활성)를 충족하는 배포물은 현행 자산 중에 **없다**. A43 배포물은
vpk를 거치지 않은 "생 publish 산출물" 기반이어야 한다.

### 1.2 csproj 배포 관련 속성 (KOTU.App.csproj)

- `WindowsPackageType=None`(unpackaged, :10), `WindowsAppSDKSelfContained=true`(:11),
  `EnableMsixTooling=true`(:34), `RuntimeIdentifiers=win-x64`(:6), `ProjectPriFileName=resources.pri`(:40).
- .NET 런타임 self-contained 여부는 csproj가 아니라 **publish 명령의 `--self-contained`**로 결정(release.yml:45).
- `Velopack 1.2.0` PackageReference(:50). libvlc 네이티브 dll 다수는 `VideoLAN.LibVLC.Windows`(:48)가 출력에 복사.
- `CopyPriToPublish` 타깃이 **`AfterTargets="Publish"`**에서 resources.pri를 publish 폴더로 복사(:95–102) — §2.3의 단일 파일 1순위 함정.

### 1.3 설정·파일 흔적 전수 (실행 시 디스크에 남는 것)

| 경로 | 언제 | 근거 |
|---|---|---|
| `%AppData%\KOTU\settings.json` | **실행만 해도 생성** — 첫 실행 구 브랜드 청소 플래그 저장(`settings.Save()`) + 이후 모든 설정·이어보기·트레이 선택 저장 | `KOTU.Core/Settings/JsonSettingsService.cs:25–28,51–58`, `KOTU.App/App.xaml.cs:129–143` |
| `%AppData%\KOTU\restart-session.json` | 관리자 재시작(A124) 직전 기록, 읽는 즉시 삭제 | `KOTU.App/Integration/RestartSessionFile.cs:58–60` |
| `%AppData%\KOTU\wallpaper.png` | 배경화면 지정(A161) 시 | `KOTU.Module.Image/ImageViewerView.xaml.cs:444–453` |
| `%TEMP%\KOTU\startup-error.log` | 시작 치명 오류 시에만 | `KOTU.App/Program.cs:22–24` |
| `%TEMP%\KOTU\subtitles`, `%TEMP%\KOTU\Archive\<guid>` | 자막 변환·압축 임시 | `KOTU.Module.Video/SubtitleCharset.cs:83`, `KOTU.Module.Archive/ArchiveView.xaml.cs:286` |

이어보기(PlaybackResumeStore)·하드웨어 상태 등은 전부 `ISettingsService` 경유라 settings.json 한 파일로 수렴한다(별도 파일 없음).

### 1.4 레지스트리 접점 전수 (쓰기 지점은 3파일 + COM 1파일)

| 접점 | 키 | 성격 | 근거 |
|---|---|---|---|
| ExplorerIntegration (파일 연결·우클릭·Capabilities) | HKCU `Software\KOTU\Capabilities`, `Software\RegisteredApplications`, `Software\Classes\KOTU.*`, `...\FileExts\<ext>\UserChoice`, `SystemFileAssociations\<ext>\shell\KOTU.*` | **opt-in** — 설정에서 켤 때만 등록. 매 실행 `ReRegisterIfExeMoved`는 "이미 등록된 경우에만" 재기록(`IsAssociationRegistered` 가드) | `KOTU.App/Integration/ExplorerIntegration.cs:66–115,267–297,387–397,564–592` |
| 구 브랜드 청소(A46) | HKCU의 ZP·WinUtil 잔재 DeleteSubKeyTree | 첫 실행 1회. 깨끗한 머신에선 지울 게 없어 실쓰기 0 — 단 완료 플래그를 settings.json에 **쓴다** | `App.xaml.cs:127–143` |
| TrayPromotion (A100) | HKCU `Control Panel\NotifyIconSettings\<항목>\IsPromoted=1` | **자동** — 트레이 아이콘 표시 시 예약 스캔·쓰기. 항목 자체는 OS가 생성 | `KOTU.App/Integration/TrayPromotion.cs:51–65` |
| DesktopWallpaper (A161) | HKCU `Control Panel\Desktop` WallpaperStyle/TileWallpaper | 사용자 명시 동작 시에만. OS 소유 키의 값 변경 | `KOTU.App/Integration/DesktopWallpaper.cs:54–69` |
| DefaultAudioInput (A164) | 레지스트리 아님 — 비공개 COM IPolicyConfig로 OS 기본 입력 장치 변경 | 사용자 명시 동작. 시스템 상태 변경(레지스트리 흔적은 OS 몫) | `KOTU.App/Integration/DefaultAudioInput.cs` |
| 읽기 전용 | 기본 앱 조회(SettingsView:392 부근), NotifyIconSettings 스캔 등 | 무해 | — |

**"흔적 0"이 막는(또는 결정이 필요한) 기능 목록**: 파일 연결·우클릭 메뉴 등록(기능 자체가
레지스트리 쓰기), 트레이 자동 승격 쓰기, 배경화면 지정(HKCU 값 2개 + %AppData% png), 관리자
재시작 창 세트 복원(%AppData% 파일), 설정·이어보기 영속(세션 한정으로 격하), 자동 업데이트
(요구 ②이므로 무방), 센서 커널축(아래 참조).

### 1.5 하드웨어 센서 축 — PawnIO의 실제 위치

저장소에는 PawnIO 관련 파일·코드가 **0건**이다(grep: `PawnIO` 무일치). 참조는
`LibreHardwareMonitorLib 0.9.6`(`KOTU.Module.Hardware.csproj:19`)뿐. LHM은 0.9.5에서
WinRing0 드라이버를 PawnIO로 교체했고(PR #1857), **PawnIO는 앱이 동봉하는 것이 아니라
사용자가 별도 설치하는 시스템 드라이버**다(pawnio.eu).
출처: https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/releases/tag/v0.9.5 ,
https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/pull/1857
→ 미설치 머신에서는 커널 의존 채널(CPU 온도·전력·클럭·팬·SSD 온도)이 null로 떨어질 뿐
앱은 정상이다(`SensorService.cs:26–29` 주석의 동작). 포터블 배포물이 드라이버 흔적을 남길
일 자체가 없다. ※ `KOTU.Module.Hardware.csproj:17–18`의 "커널 드라이버를 동봉·자동 로드"
주석은 WinRing0 시절(≤0.9.4) 기술로, 0.9.6 실동작과 어긋난다(문서 갱신 후보).

---

## 2. .NET 단일 파일 실현성

### 2.1 공식 지원 상태 — 지원됨 (WASDK 1.5+, 조건부)

Microsoft 공식 문서: **unpackaged + self-contained WinUI 3 앱은 Windows App SDK 1.5부터
`PublishSingleFile`을 지원**한다. 필수 속성 6종이 명시돼 있고 누락 시 빌드 에러를 내는 검증
타깃(`WindowsAppSDKSingleFileVerifyConfiguration`)이 있다:
`WindowsPackageType=None` · `WindowsAppSDKSelfContained=true` · `SelfContained=true` ·
`EnableMsixTooling=true` · `IncludeAllContentForSelfExtract=true` · `PublishSingleFile=true`.
출처: https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/unpackage-winui-app

KOTU는 앞의 3종을 이미 충족한다(§1.2). 나머지 3종은 csproj 수정 없이 **포터블 전용 CI 잡의
`dotnet publish -p:` 인자**로 줄 수 있다(설치본 빌드와 완전 분리 — 규칙 "설치본과 별개 배포물"과 합치).

### 2.2 IncludeAllContentForSelfExtract — 사용자 아이디어와 동형

- 동작: 모든 의존물을 exe에 번들하고 **첫 실행 시 임시 폴더로 자체 추출** 후 실행. 문서가
  명시: "the app is not a zero-extraction binary". 사용자 아이디어("실행 시 임시 공간 자체
  압축 해제")가 곧 .NET의 공식 메커니즘이다 — 별도 래퍼 불필요.
- 추출 위치: Windows 기본 `%TEMP%\.net\<앱이름>\<bundle-id>` (`DOTNET_BUNDLE_EXTRACT_BASE_DIR`
  환경 변수로 변경 가능). **재사용형 캐시라 자동 삭제되지 않는다** — 잔여 흔적 1건은 구조적으로
  남는다(§4.3의 정의 문제). 버전이 바뀌면 bundle-id가 바뀌어 새 폴더가 하나 더 생긴다.
  출처: https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview ,
  https://github.com/dotnet/designs/blob/main/accepted/2020/single-file/extract.md
- WinRT 활성화는 `WindowsAppSdkUndockedRegFreeWinRTInitialize` 자동 초기화가 추출 폴더를
  찾아 준다(옵트아웃 시 `MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY` 수동 설정 필요 — 우리는 옵트아웃 안 함).

### 2.3 실행 불가 리스크 — CI가 못 잡는 부류 (실기기 확인 필수)

- 실보고 다수: unpackaged self-contained 단일 파일이 **빌드는 되는데 실행이 안 되는** 사례 —
  `Microsoft.WindowsAppRuntime.dll`을 추출 폴더가 아니라 exe 옆에서 찾다 죽는 FileNotFound 등.
  https://github.com/microsoft/microsoft-ui-xaml/issues/10173 ,
  https://github.com/microsoft/microsoft-ui-xaml/issues/9758
  (상당수가 필수 속성 세트 불완전 — VS 게시 UI에는 `IncludeAllContentForSelfExtract` 항목이
  없다 — 로 보이나, 전부는 아니다.) CI는 컴파일만 하므로 이 부류는 릴리스 후 실기기에서만 판별된다.
- **저장소 고유 함정 1순위 — resources.pri**: `CopyPriToPublish`가 `AfterTargets="Publish"`
  (`KOTU.App.csproj:95–102`)라서 **단일 파일 번들이 만들어진 뒤에** pri를 publish 폴더로
  복사한다 → pri가 번들 밖 루스 파일로 남고, exe 하나만 배포하면 v0.5.5와 동형의
  XamlParseException 즉사가 재현될 수 있다. 대응 후보: 번들 전에 pri를 Content로 편입시키는
  타깃 추가, 또는 최소 폴백 "exe + resources.pri 2파일 배포"(요구 ① 타협).
- **7z.dll**: 현행은 CI가 publish 후 복사(release.yml:47–55)라 번들에 못 들어간다. 단,
  `KOTU.Module.Archive.csproj:33–35`에 이미 `runtimes\win-x64\7z.dll`을 출력에 복사하는
  조건부 항목이 있다 — 포터블 잡에서 **빌드 전에** 그 자리에 7z.dll을 놓으면 번들에 포함되고,
  로드는 `AppContext.BaseDirectory` 기준(`SevenZipBackend.cs:21`)이라 추출형에서 그대로 성립한다
  (단일 파일에서 BaseDirectory = 추출 폴더).
- **libvlc**: 네이티브 dll·plugins 트리 다수. `Core.Initialize()`는 앱 폴더 기준 탐색이라
  추출형이면 성립이 기대되지만, plugins 하위 디렉터리 구조가 번들·추출에서 보존되는지는
  실기기 확인 포인트다(실패 시 동영상·음악 모듈만 죽고 나머지는 정상인 형태).
- **LHM PawnIO 축**: §1.5 — 파일 동봉이 없으므로 단일 파일화에 장애 없음.

### 2.4 대안: 셀프 추출 래퍼 (7z SFX·warp-packer류)

publish 폴더를 SFX 스텁으로 싸는 방식. 관례상 리스크: ① 서명 없는 자기추출 스텁은 **백신
오탐 단골**(런타임 압축 해제 + 프로세스 스폰 패턴), ② 매 실행 전체 추출이라 기동 느림 또는
잔여 폴더, ③ 스텁이 낯선 도구 의존(공급망·유지보수). .NET 공식 경로(§2.1)가 있는 이상
후순위 — 공식 경로가 실기기에서 최종 실패할 때의 예비안으로만.

---

## 3. 업데이트 비활성

### 3.1 현행 업데이트 경로

- `KOTU.App/Integration/UpdateService.cs:15–26` — `UpdateManager.IsInstalled`가 관문.
  주석(:8–9)이 이미 명시: "수동 zip 실행에서는 조용히 비활성".
- `KOTU.App/Integration/UpdateCoordinator.cs:126–127` — `IsAvailable = UpdateService.IsUpdatableBuild`,
  false면 **2분 타이머 자체를 만들지 않는다**. `CheckNowAsync():160`도 `!IsAvailable`이면 즉시 반환 —
  설정 화면 진입 1회 확인(`SettingsView.xaml.cs:960`)까지 같은 관문으로 막힌다.
- `Program.cs:38–40`의 `VelopackApp.Build().Run()`은 Velopack 관리 밖 실행에서 무해 통과.

### 3.2 판정 — "포터블 빌드 플래그" 없이도 요구 ②는 성립

vpk를 거치지 않은 생 publish 산출물(단일 exe 포함)은 `IsInstalled=false`라 **코드 수정 0으로
업데이트 확인·다운로드·적용 전부가 비활성**이다. 설정 화면은 업데이트 섹션을 숨기지 않고
비활성으로 보여주는 것이 기존 확정 사양(UpdateCoordinator 주석 :39–40). 선택 과제: 포터블
빌드에서 문구를 "portable build — updates disabled"로 바꾸는 소소한 UI 개선 정도.

### 3.3 Velopack 미포함 빌드 가능성

Velopack API 침투는 4파일: `Program.cs`(:38–40), `UpdateService.cs`(전체),
`UpdateCoordinator.cs`(`Velopack.UpdateInfo` 타입 :102,165), `SettingsView.xaml.cs`(:964).
패키지 참조를 빼려면 이 4파일 조건부 컴파일이 필요한데, **얻는 것은 exe 용량 소폭 감소뿐**이고
런타임 동작은 §3.2로 이미 원하는 상태다 → 미포함 빌드는 권장하지 않음(불필요한 분기 유지비).

---

## 4. 흔적 0 모드

### 4.1 설정의 메모리 스왑 지점

- DI 등록 한 줄이 스왑 지점: `App.xaml.cs:34` `services.AddSingleton<ISettingsService, JsonSettingsService>()`.
  모든 모듈·셸이 생성자 주입으로 이 인터페이스만 받으므로(§1.3) `MemorySettingsService`
  (Get/Set 딕셔너리, `Save()` no-op) 하나로 전 저장 경로가 세션 메모리化된다.
- 계약 주의: `ISettingsService.FilePath`(`ISettingsService.cs:10`)는 설정 화면 표기·
  "Open settings.json" 열기에 쓰인다(`SettingsView.xaml.cs:745,766`) — 메모리 구현은 표시
  문자열(예: "(in-memory — portable build)")을 돌려주고 열기 버튼은 비활성 처리가 필요하다.
- 포터블 판별: 빌드 상수(`-p:DefineConstants` 추가) 방식이 가장 단순·오판 없음(단일 파일
  여부 런타임 감지·마커 파일 방식보다). — 구현 시 결정 사항.

### 4.2 메모리 설정으로도 남는 것 (전수)

| 잔여 | 분류 | 처리 |
|---|---|---|
| `%TEMP%\.net\KOTU\<id>` 추출 폴더 | .NET 런타임이 생성, 자동 삭제 없음 | 구조적 — 정의에서 제외(§4.3) 또는 종료 시 자기 삭제 시도(재사용 캐시 포기, 기동 느려짐) |
| `%TEMP%\KOTU\*` (오류 로그·자막·압축 임시) | 임시 폴더 성격 | 무해 판정 권고(OS 정리 대상 위치) |
| `NotifyIconSettings` 항목 생성 | **OS가** 트레이 아이콘 표시 시 자체 기록 | 앱이 막을 수 없음 — 범위 밖 명시. 단 `TrayPromotion`의 IsPromoted **쓰기**는 앱 소행 → 포터블 모드에서 스킵할 지점(`TrayPromotion.Request()` 호출부 가드) |
| `restart-session.json` | 관리자 재시작 사용 시 %AppData% 생성 | 포터블 모드에서 임시 폴더로 우회 또는 기능 유지·문서화 — 결정 필요 |
| 배경화면 지정·기본 입력장치·파일 연결 등록 | **사용자가 명시적으로 시킨** 시스템 변경 | 흔적이 아니라 기능의 목적 — 포터블에서도 허용하되 문서화 권고(숨김 여부는 확인 질문) |
| 레지스트리 읽기 전용 접근 | 조회뿐 | 무해 판정 |

### 4.3 "완전 0"의 현실적 정의 (제안)

**"KOTU 포터블은, 사용자가 명시적으로 시키지 않는 한 이 컴퓨터에 아무것도 영구히 쓰지
않는다."** 구체화: ⓐ 앱이 자동으로 만드는 영구 흔적(%AppData%\KOTU 일체, HKCU 자동 쓰기
= 구 브랜드 청소·재등록·TrayPromotion) = 0 보장, ⓑ OS·런타임이 만드는 부수 기록(.NET 추출
캐시, NotifyIconSettings 항목, prefetch류) = 범위 밖 명시, ⓒ 사용자가 버튼으로 시킨 시스템
변경(배경화면·기본 장치·파일 연결) = 흔적이 아니라 결과물. 임시 폴더(%TEMP%) 사용은 허용.
— 이 정의면 코드 변경이 "메모리 설정 스왑 + 자동 쓰기 3지점 가드"로 수렴하고 검증도 가능해진다.

---

## 5. 권고안

### 5.1 배포 형태 후보 비교

| | ⓐ PublishSingleFile + SelfExtract | ⓑ SFX 래퍼(7z SFX 등) | ⓒ 생 publish zip + 포터블 플래그만 |
|---|---|---|---|
| 요구 ① exe 하나 | **충족**(첫 실행 추출) | 충족(매 실행/최초 추출) | 미충족(zip 해제 필요) — 타협 |
| 요구 ② 업데이트 0 | 충족(비 Velopack이면 자동, §3.2) | 충족(동일) | 충족(동일) |
| 요구 ③ 흔적 0 | §4 작업 동일 + 추출 캐시 잔존(정의 ⓑ로 흡수) | 동일 + 추출 잔여 | 동일(추출 캐시 없음 — 가장 깨끗) |
| 공식 지원 | MS 공식(WASDK 1.5+) | 비공식 관례 | 완전 기존 경로 |
| 주 리스크 | 실행 불가 부류(#10173) + pri/7z.dll 번들 편입 — 실기기 확인 필수 | 백신 오탐·서명 부재 | 없음(현행 검증 재사용) |
| CI 추가 비용 | publish 잡 스텝 3~4개 + 자산 1개(~수백 MB exe) | SFX 도구 도입·유지 | zip 스텝 1개 |

**권고: ⓐ를 본선으로, ⓒ를 폴백으로.** ⓑ는 백신 오탐 리스크 때문에 공식 경로가 실기기에서
최종 실패할 때만. ⓐ·ⓒ 모두 §3.2에 의해 업데이트 비활성은 공짜이고, §4의 흔적 0 작업
(메모리 설정 + 자동 쓰기 가드)은 형태와 무관하게 공통이다.

### 5.2 택일 질문 문안 (사용자 제시용)

> A43 포터블: MS 공식 단일 exe(첫 실행 시 %TEMP%에 자체 추출 — 추출 캐시 하나는 남고, 실기기
> 실행 확인이 한 번 필요합니다)로 갈까요, 아니면 확실히 동작하는 "무Velopack zip + 흔적 0
> 모드"(exe 하나는 포기)로 갈까요? — (a) 단일 exe 본선 + 실패 시 zip 폴백 / (b) 처음부터 zip

### 5.3 빌드 파이프라인 추가 비용 개요 (ⓐ 기준)

release.yml에 잡(또는 스텝 묶음) 1개 추가: ① 7z.dll을 `src/KOTU.Module.Archive/runtimes/win-x64/`에
**빌드 전** 배치 ② `dotnet publish -p:PublishSingleFile=true -p:SelfContained=true
-p:IncludeAllContentForSelfExtract=true -p:DefineConstants=PORTABLE_BUILD`(별도 출력 폴더)
③ resources.pri 번들 편입 확인(§2.3 함정 — 검증 스텝 신설) ④ 산출 exe를 릴리스 자산
`KOTU-win-Standalone.exe`(가칭)로 업로드. 설치본 빌드와 출력 폴더를 분리하면 기존 단계는 무변경.
빌드 시간 +수 분, 릴리스 자산 +1(self-contained 전체가 든 단일 exe — 현행 Portable.zip과 유사 체급).

---

### 부록 — 이번 조사에서 발견한 문서·주석 어긋남

1. `KOTU.Module.Hardware.csproj:17–18` "커널 드라이버를 동봉·자동 로드" — LHM 0.9.5+의
   PawnIO 체제(별도 설치)와 어긋남(§1.5). 문서 갱신 후보.
2. 릴리스 본문·통념상 "Portable.zip = 수동 zip"으로 읽히기 쉽지만 실제로는 자동 업데이트되는
   Velopack 자산(§1.1) — A43 등재문("현행 zip + 포터블 모드 플래그만" 후보)은 실제로는
   **새 zip 신설**을 뜻한다.
