# KOTU — 올인원 Windows 유틸리티 아키텍처 설계

최초 설계 초안: v0.1.0 · 2026-08-03. 이후 8~11장(스레드 모델·중첩 호스팅·브랜드 에셋·UI 원칙)이
증축되었고, 1~7장은 **당시 설계 판단의 기록**으로 남긴다 — 구현의 정본은 언제나 코드다.
현행 상태·남은 작업은 `docs/REQUIREMENTS.md`, 이어받기 요약은 `docs/HANDOVER.md`를 본다.

## 1. 컨셉

윈도우 새로 설치 후 각각 따로 깔던 필수 앱들을 하나의 앱으로 통합한다.

- 1차 범위: 압축 프로그램, 이미지 뷰어(←/→ 키 탐색), 동영상 플레이어
- 2차 범위(확장): CPU-Z류 하드웨어 정보, HWMonitor류 센서 모니터링, 스트레스 테스트/벤치마크
- 원칙: 각 기능은 이미 검증된 라이브러리를 통합하고, 우리는 통합 셸과 UX에 집중한다.

## 2. 기술 스택

| 영역 | 선택 | 근거 |
|---|---|---|
| 언어/런타임 | C# / .NET 8+ | 생산성, 필요한 라이브러리 전부 존재 |
| UI | WinUI 3 (Windows App SDK) | Windows 네이티브, 모던 UI |
| 압축 | 7z.dll 래퍼(SevenZipSharp 계열) + SharpCompress 보조 | 7-Zip 엔진이 포맷 커버리지 최강(zip/7z/rar/tar/gz…) |
| 이미지 | WIC(Windows 내장 코덱) + Magick.NET 확장 | jpg/png/gif/bmp/webp는 WIC로 충분, psd/heic 등은 Magick |
| 동영상 | LibVLCSharp (libvlc) | 코덱 내장(별도 코덱 설치 불필요), 실전 검증 |
| HW 정보/센서 | LibreHardwareMonitor (MPL 2.0) | CPU-Z + HWMonitor 영역을 하나로 커버 |
| 스트레스 테스트 (**미구현**) | 자체 구현(정수/부동소수 연산, 메모리 대역폭) | 단순 부하 생성은 자체 구현이 적합 |
| 배포 | unpackaged EXE + Velopack(자동 업데이트) | HW 센서용 커널 드라이버 로드에 MSIX 제약이 있어 unpackaged 우선 |

라이선스 주의: 7-Zip(LGPL), libvlc(LGPL) → dll 동적 링크로 사용(정적 링크 금지). LibreHardwareMonitor(MPL) → 해당 파일 수정 시 공개 의무만 있음. 상용 배포에도 문제 없는 조합.

## 3. 전체 구조: 셸 + 모듈 플러그인

```
┌─────────────────────────────────────────────┐
│  App Shell (WinUI 3)                        │
│  네비게이션 · 창 관리 · 설정 UI · 단일 인스턴스   │
├─────────────────────────────────────────────┤
│  Core (공통 서비스 계층)                       │
│  DI 컨테이너 · 설정 저장 · 로깅 · 테마          │
│  파일 타입 라우터 · 파일 연결(OS 등록) · 업데이트  │
├────────┬────────┬────────┬────────┬───────┤
│Archive │ Image  │ Video  │ Audio  │ HW    │
│Module  │ Viewer │ Player │ Player │ Info· │
│        │ Module │ Module │ (A10)  │ Mon·  │
│        │        │        │ Module │ Stress│
└────────┴────────┴────────┴────────┴───────┘
```

위 그림은 초안 시점의 것이다. 현재는 **Document 모듈**(4.3c)과 **All Readable 모듈**(9장)이 더해져
모듈 슬롯이 일곱이다.

핵심 규칙:

- 모듈은 Core에만 의존하고 모듈끼리는 직접 참조하지 않는다(모듈 간 통신은 Core의 이벤트/라우터 경유).
- 각 모듈은 `IModule` 계약을 구현하는 독립 프로젝트(.csproj)다. 이 경계가 곧 하위 에이전트의 작업 경계다.

```csharp
public interface IModule
{
    string Id { get; }                       // "archive", "image", "video", ...
    string[] SupportedExtensions { get; }    // 파일 라우팅용
    Control CreateView(OpenContext ctx);     // 셸에 꽂히는 뷰
}
```

- 파일 라우팅: 탐색기에서 파일 더블클릭 → 단일 인스턴스로 인자 전달 → 확장자로 담당 모듈 결정 → 해당 뷰 활성화. 이게 이 앱의 중심 UX다.

### 솔루션 구성

```
KOTU.sln                 # 실행 파일은 KOTU.exe (AssemblyName, A64/v0.88.0)
├─ src/KOTU.App          # 셸 (실행 파일)
├─ src/KOTU.Core         # 계약(인터페이스) + 공통 서비스
├─ src/KOTU.Module.Archive
├─ src/KOTU.Module.Image
├─ src/KOTU.Module.Video
├─ src/KOTU.Module.Audio      # A10(v0.75.0)에서 Video로부터 분리
├─ src/KOTU.Module.Document   # 텍스트 편집·마크다운/HTML 렌더 뷰·PDF 뷰어 (초안 이후 신설 — 아래 4.3c)
├─ src/KOTU.Module.Hardware   # Phase 5 (정보·모니터링 구현, 스트레스는 미구현)
├─ src/KOTU.Module.AllReadable # A59(v0.113.0) 통합 모듈 — 아래 9장(중첩 호스팅)
└─ tests/                   # 모듈별 단위 테스트
```

## 4. 모듈별 기능 명세 (필수 / 옵션)

### 4.1 압축 (Archive)

- 필수: zip·7z·rar·tar·gz 해제 / zip·7z 생성 / 압축 내부 탐색(풀지 않고 미리보기) / 드래그&드롭 / 탐색기 컨텍스트 메뉴("여기에 풀기", "압축하기") / 암호 걸린 파일 처리 / 한글 인코딩(CP949↔UTF-8) 자동 처리
- 옵션: 분할 압축, 압축률 선택, 무결성 검사
  · ~~압축 내부 파일 즉시 열기~~ = 구현됨 — 다만 **모듈 라우팅이 아니라 임시 폴더에 1건만 풀어
  Windows 기본 연결 앱으로 연다**(사용 설명서 2.6)

### 4.2 이미지 뷰어 (Image)

- 필수: jpg·png·gif(애니)·bmp·webp 표시 / ←→ 키로 폴더 내 이전·다음 / 확대·축소(휠)·패닝·창맞춤 / 회전 / 전체화면 / 삭제(휴지통) / EXIF 회전 반영 / 인쇄(A211, v0.220.0~v0.223.0에서 추가)
- 옵션: heic·raw(Magick.NET), 슬라이드쇼, 간단 편집(크롭·리사이즈)
  · ~~psd(Magick.NET)~~ = v0.34.0에서 구현(Magick 확장 — 지원 확장자에 포함)
  · ~~EXIF 정보 패널~~ = A150(v0.181.0)·A200(v0.207.0)에서 구현(우측 정보 패널 16키 + 하단 바 요약)
  · ~~배경화면 설정~~ = A161(v0.174.0)에서 구현(우클릭 "Set as desktop background")

### 4.3 동영상 플레이어 (Video)

- 필수: mp4·mkv·avi·webm 등 재생(libvlc가 커버) / 재생·일시정지·시킹 / 볼륨·배속 / 자막(srt·smi, 한글 인코딩 처리) / 전체화면 / 이어보기(마지막 위치 기억) / ←→ 키 탐색(초 단위 점프) / 빈 상태 ▶=내장 화면·스피커 테스트 클립(v0.11.0~v0.13.0)
- 옵션: 자막 싱크·폰트 조절, 오디오 트랙 선택, 스크린샷, 구간 반복
  · ~~재생목록~~ = A11(v0.210.0~v0.212.0)에서 폴더 연속 재생 + 루프 3모드(끄기·목록 반복·1파일
  반복, 반복 횟수 선택)로 구현. 별도 목록 편집 UI는 없다
- 음악 파일 재생(mp3·flac·wav 등)은 A10(v0.75.0)에서 오디오 모듈(KOTU-audio)로 분리

### 4.3b 오디오 플레이어 (Audio — A10, v0.75.0에서 Video로부터 분리)

- 필수: mp3·flac·wav·ogg·opus·m4a·aac·wma 재생(libvlc, 파형 시각화 visual/scope 인스턴스 상시 켬) / 재생·일시정지·시킹 / 볼륨·배속 / 이어듣기 / 전체화면 / ♪+파일명 오버레이
  · 시각화는 A268(v0.266.0)에서 **5종 선택**(Off·Scope 기본·Spectrum·Spectrometer·VU)으로 확장
  · 출력 장치 선택·이퀄라이저도 추가됨(사용 설명서 2.3)
- 옵션: 태그(ID3) 표시
  · ~~재생목록·루프~~ = A11(v0.210.0~v0.212.0)에서 Video와 공용 사양으로 구현

### 4.3c 문서 (Document — 초안 이후 신설)

설계 초안에는 없던 모듈. 텍스트(txt·md·log·ini·html) 편집 + PDF 보기를 한 모듈이 맡는다.

- 텍스트: 인코딩 감지·보존 저장(A37, v0.80.0) / 저장 전 경고·저장 후 검증 / 편집 ↔ 보기 모드
- 렌더 뷰: 마크다운 렌더 · ~~HTML 진짜 렌더~~ = A248(v0.256.0, WebView2 — 런타임 부재 시 잠금 소스 뷰 폴백)
- PDF: 연속 스크롤 뷰어(A16, v0.81.0 — Windows.Data.Pdf)
- 인쇄: A211(v0.220.0~v0.223.0) — 텍스트·마크다운(보고 있는 뷰 그대로)·PDF(페이지 범위 지정)

### 4.4 하드웨어 (Phase 5)

- ~~정보(CPU-Z류)~~: CPU·메인보드·RAM·GPU·스토리지 스펙 표시 — 구현됨(우측 패널 사양 목록 7분류)
- ~~모니터(HWMonitor류)~~: 온도·클럭·팬·전압·사용률 실시간 그래프 — 구현됨(센서 타일 10종·확대 그래프·트레이 표시)
- 스트레스(**미구현**): CPU 전코어 부하, 메모리 부하, 모니터링과 연동한 안정성 테스트
- 제약: 센서 접근에 관리자 권한 + 커널 드라이버 필요 → 이 모듈만 권한 상승 요구, 나머지는 일반 권한 유지

## 5. 개발 로드맵과 에이전트 분담

원칙: Phase 0에서 Core 인터페이스를 확정·동결하면, 이후 모듈들은 하위 에이전트에게 병렬로 맡길 수 있다.

| Phase | 내용 | 담당 | 의존 |
|---|---|---|---|
| 0 | 셸 + Core: 솔루션 뼈대, DI, 설정, 파일 라우터, 단일 인스턴스, IModule 계약 확정 | 메인(직접) | — |
| 1 | 이미지 뷰어 모듈 (가장 단순 → 계약 검증 겸용) | 에이전트 A | 0 |
| 2 | 압축 모듈 | 에이전트 B | 0 (1과 병렬) |
| 3 | 동영상 모듈 | 에이전트 C | 0 (1·2와 병렬) |
| 4 | 통합·품질: 파일 연결 OS 등록, 컨텍스트 메뉴, 설정 UI 통합, 설치본·자동 업데이트 | 메인(직접) | 1–3 |
| 5 | 하드웨어 정보 → 모니터링 → 스트레스 (순차) | 에이전트 D | 4 |

- Phase 0는 병렬화하지 않는다 — 계약이 흔들리면 전 모듈이 흔들린다.
- 각 모듈 완료 기준: 필수 기능 전부 + 단위 테스트 + 셸에서 파일 열기 동작 확인.
- 커밋 규칙: 변경마다 버전 명기하여 커밋 (예: `v0.2.0 image-viewer: 방향키 탐색 구현`).

> **현황**: Phase 0~5는 모두 완주했고(스트레스 테스트만 미구현), 초안에 없던 Document·All Readable
> 모듈이 더해져 현재 7모듈이다. 위 표는 **초기 분담 기록**으로 남긴다 — 지금의 개발 방식(오케스트레이터
> + 서브에이전트 직렬)과 커밋·릴리스 규칙의 정본은 `CLAUDE.md`, 남은 작업은 `docs/REQUIREMENTS.md`.

## 6. 주요 리스크 (초안 시점 목록 + 결말)

1. ~~WinUI 3 + LibVLCSharp 조합~~ — **해소**: LibVLCSharp.WinUI로 성립. 대체안(SwapChainPanel 직접 렌더·Flyleaf)은 쓰지 않았다.
2. 네이티브 dll 배포 — 7z.dll, libvlc(약 80MB)로 배포 용량 증가. **x64 전용 유지**로 단순화한 상태 그대로다.
3. 하드웨어 센서 권한 — **일부 해소**: LibreHardwareMonitor 0.9.5+가 WinRing0 대신 별도 설치형 서명 드라이버 **PawnIO**를 쓴다(A47). 드라이버는 동봉·설치하지 않고, 읽을 수 없을 때 화면에서 관리자 재시작 또는 PawnIO 안내를 준다. unpackaged 배포 선택의 근거는 그대로.
4. ~~탐색기 컨텍스트 메뉴~~ — **해소**: sparse package 없이 **현재 사용자 범위(HKCU) 등록**으로 해결(A38 계열). 스위치를 끄면 흔적 없이 해제되고 관리자 권한이 필요 없다.

## 7. 다음 단계

초안의 3개 항목(설계 확정 · Phase 0 착수 · 스파이크 2건)은 모두 끝났다. 지금의 "다음 단계"는
이 문서가 아니라 `docs/REQUIREMENTS.md`(백로그·A번호)와 `docs/HANDOVER.md`(현재 상태)가 들고 있다.

## 8. 스레드 모델 (v0.62.0, A42)

원칙: **UI 스레드는 렌더·입력만.** 모듈의 장기 작업은 Core의 스레딩 계약을 거쳐 전용 워커에서 돌고, 뷰는 완료·진행률을 디스패치로 받아 그리기만 한다. (이 장 = "누가 어디서 도는가"의 정본. 상위 원칙·금지 목록·경계 규칙 = 11장(A178).)

### 8.1 Core 계약 (`KOTU.Core.Threading`)

| 타입 | 역할 |
|---|---|
| `ModuleWorker` | 모듈(뷰) 전용 직렬 워커. 이름 있는 전용 스레드 1 + FIFO 큐. `Run`(완료 Task)/`Post`(뒷정리 fire-and-forget) |
| `WorkContext` | 작업에 전달되는 실행 맥락 — 취소(`Cancellation`)·진행률(`Progress`, 없으면 no-op) 통일 |
| `PollingWorker<T>` | 주기 폴링 루프. 구독 없으면 휴면, 첫 구독 시 즉시 1회, `Poke()`로 간격 건너뛰기 |

계약 한 형태: **요청** = `worker.Run(ctx => 작업, ct, progress)` / **취소** = `CancellationToken` / **진행률** = `IProgress<double>`(UI 스레드에서 만든 `Progress<T>`는 자동 마샬링) / **완료** = 반환 `Task`(UI에서 await → UI로 복귀). 워커 큐는 직렬이라 같은 워커의 작업은 겹치지 않고 순서가 보장된다.

### 8.2 스레드 목록 (누가 도는가)

| 스레드 | 수 | 우선순위 | 수명·비고 |
|---|---|---|---|
| UI 스레드 | 창마다 1 | Normal | WinUI 3 디스패처. 렌더·입력·결과 반영만 |
| `KOTU hardware poller` | 프로세스 1 (**공유**) | **BelowNormal** | 50/200/500/1000/2000/5000ms 폴링(A73 선택, 기본 500): 센서(LHM)는 매 주기, WMI 스펙은 2초 캐시, SMART는 10초마다(A17). H/W 뷰 구독 0이면 휴면 |
| `KOTU explorer worker` | 페인마다 1 | Normal | 폴더 스캔·썸네일 추출. Unloaded 시 정리 |
| `KOTU archive worker` | 뷰마다 1 | Normal | 목록/해제/생성/항목 미리보기 |
| `KOTU image worker` | 뷰마다 1 | Normal | 파일 읽기·WIC 메타데이터·Magick 디코드·EXIF 정보 |
| `KOTU video worker` | 뷰마다 1 | Normal | libvlc 생성·해제, 자막 탐지·CP949 변환 |
| `KOTU audio worker` | 뷰마다 1 | Normal | libvlc(시각화 인스턴스) 생성·해제 (A10) |
| `KOTU document worker` | 뷰마다 1 | Normal | 텍스트 읽기(인코딩 감지)·저장(인코딩 보존, A37) |
| (All Readable 전용 워커 없음) | — | — | 자식 모듈 뷰의 워커를 그대로 쓴다 — 자식이 바뀌면 이전 워커도 함께 정리(9장) |
| `KOTU drive strip worker` | 하단 바 드라이브 줄마다 1 (= 모듈 뷰마다 1) | **BelowNormal** | 드라이브 열거·용량(`DriveInfo`) 30초 주기 + 종류 WMI 1회 캐시 (A22, v0.108.0). 줄이 숨겨지면(파일 열림) 타이머 정지, 뷰 Unloaded 시 정리 |
| `KOTU settings worker` | 설정 뷰마다 1 | Normal | 탐색기 연결 등록·해제, UserChoice 쓰기(A38), 기본 앱 개수 조회 (A77, v0.106.0). 모듈별로 나누지 않는다 — Capabilities 키를 모듈들이 공유해 동시 쓰기가 위험 |
| libvlc 내부 스레드 | libvlc 관리 | — | 디코드·이벤트 콜백. 이벤트는 `Dispatch()`로 UI 이관 |
| .NET 스레드풀 | 런타임 관리 | — | await 연속, 닫힌 워커의 `Post` 폴백 |

### 8.3 작업 → 스레드 매핑 (어느 작업이 어디서 도는가)

| 작업 | 스레드 | UI 스레드가 하는 일 |
|---|---|---|
| 하드웨어 WMI 스펙 + LHM 센서 수집(50~5000ms 선택, A73) | hardware poller | 스펙은 dedup 후 트리 반영, 센서 카드·그래프는 매 프레임 갱신 |
| 탐색기 폴더 스캔 / 썸네일 추출 | explorer worker | 목록 채우기 / 비트맵 표시 |
| S1 중앙 썸네일 뷰 타일(`ThumbnailExplorer`, A93 — S4 '오픈 파일' 오버레이(A90)도 같은 컨트롤의 **별도 인스턴스**로 재사용, 목록 경로 동일) | 전용 워커 없음 — 목록은 좌 도크 리스트(ExplorerPane)의 결과를 공유, 이미지 미리보기는 XAML `BitmapImage` 비동기 디코드(DecodePixelWidth 256) | 타일 구성·크기 재계산(floor(실폭/열수)) |
| 압축 목록·해제·생성 | archive worker | 진행률 바·완료 상태 |
| 이미지 파일 읽기·메타데이터(WIC)·psd(Magick) | image worker | `SetSourceAsync` 표시 |
| 영상 libvlc 생성·해제 | video worker | 뷰 연결(`Vlc.MediaPlayer`) |
| 음악 libvlc(파형 시각화) 생성·해제 (A10) | audio worker | 뷰 연결(`Vlc.MediaPlayer`) |
| 자막 탐지·CP949→UTF-8 변환 | video worker | 플라이아웃·`AddSlave` 적용 |
| 문서 텍스트 읽기·저장(A37) | document worker | 본문 표시·수정됨 표시 갱신 |
| PDF 로드·페이지 렌더(A16, Windows.Data.Pdf) | WinRT 비동기(OS 관리) | 페이지 비트맵 표시(가상화 지연 렌더) |
| 드라이브 목록·용량(`DriveStatus.Collect`) + 종류 WMI 조회(`PhysicalDiskKinds`, 프로세스 1회 캐시) | drive strip worker | 공용 드라이브 줄(`DriveStrip`) 항목·막대 그리기, 넘치면 마퀴 |
| 탐색기 연결 등록·해제 + 기본 앱 지정(A38)·개수 조회 (A77) | settings worker | 진행 링·`Registering... (n/m)` 텍스트, 완료 후 "Default app for n/m extensions"·결과 문구 반영 |
| libvlc 재생 이벤트(시간·상태) | libvlc 스레드 | `Dispatch()` 경유 슬라이더·라벨 갱신 |

### 8.4 공유/분리·예산 정책 (사용자 확정 2026-08-07)

- **Hardware 폴러만 프로세스 공유**: 창이 몇 개든 WMI·센서 수집은 1회(구독 N). LHM(커널 드라이버) 접근도 이 스레드 한 곳뿐(A17) — 뷰의 그래프 이력 조회는 별도 잠금이라 수집에 안 막힌다. 나머지 파일 모듈 워커는 **뷰(창)별 분리** — 창 A의 압축 해제가 창 B를 기다리게 하지 않는다.
- **스레드 예산**: 배경 폴링은 BelowNormal로 재생·UI와 CPU를 다투지 않는다. 워커는 유휴 시 큐 대기(비용 0). 겹침 방지는 직렬 큐·단일 루프가 구조적으로 보장.
- **수명**: 뷰 Unloaded → `Dispose()`(큐만 닫음, Join 없음 — 느린 I/O가 UI 해제를 막지 않게). 남은 작업은 워커가 마저 실행. 닫힌 뒤의 `Post`(네이티브 해제 등)는 스레드풀 폴백으로 실행을 보장.

## 9. 중첩 호스팅 — All Readable 모듈 (A59, v0.113.0)

원칙: **셸의 모듈 슬롯 하나를 통합 모듈이 차지하고, 그 안에서 다시 모듈 뷰를 갈아 끼운다.** 창·오버레이·시작 메뉴는 계속 All Readable의 것이고, 파일 형식에 따라 바뀌는 것은 **센터와 하단 바 두 곳뿐**이다.

```
셸(MainWindow)                     All Readable 뷰                자식 모듈 뷰
 ModuleHost      ──────────────►   ChildHost      ────────────►   (Image/Video/Audio/Document/Archive)
 ModuleBarHost   ◄── TakeBottomBar ─ StatusBar ──► ChildBarHost ◄─ TakeBottomBar ─
 좌/우 오버레이  ◄── 필터 = All Readable.SupportedExtensions(자식 확장자 합집합)
```

- **모듈 등록 순서**: All Readable은 **맨 마지막**에 등록한다. `FileTypeRouter`는 등록 순서가 우선순위라, 확장자 합집합을 가진 이 모듈이 앞에 오면 탐색기 더블클릭이 전부 여기로 빨려 들어간다.
- **자식 선택**: `KOTU.Core.Routing.AllReadableRouting`(순수 함수, 단위 테스트 대상) — 자식 후보는 "확장자가 있는 파일 모듈 − 자기 자신"이라 정보(H/W) 모듈과 자기 자신은 자동으로 빠진다(중첩 재귀 차단).
- **셸 계약은 전부 위임**: `IContentStateSource`(자식이 연 파일 중계) · `IContentInfoProvider`(우측 정보 오버레이) · `ICloseGuard`(문서 미저장 가드 A37) · `IBottomBarProvider` · `IDriveStripHost`(A22) · `ITrayStatusProvider`(트레이 표시 내용 A54 — 자식 교체 시점에도 이벤트를 쏴 옛 값이 남지 않게 한다) · `IPlaybackStateSource`(영상 자식 재생 상태 A186) · `IPrintPageProvider`(인쇄 A211 — 하단 바를 통째로 얹는 구조라 자식의 인쇄 버튼도 여기 뜬다. 중계가 없으면 그 버튼과 셸 Ctrl+P가 둘 다 무동작이 된다). 셸이 새로 아는 계약은 `IFileOpenTarget` 하나뿐 — "이 파일 네가 열래?"를 라우팅보다 먼저 묻는 지점이다.
- **워커 수명 규칙(A42 연장)**: 통합 모듈은 자기 워커를 만들지 않고 자식 뷰의 워커를 그대로 쓴다. 그래서 **자식을 트리에서 떼는 것이 곧 정리**다 — 자식 교체·뷰 Unloaded 양쪽에서 ① 이벤트 구독 해제 → ② **하단 바 조각 제거**(셸 하단 바 트리에 얹혀 있어 센터를 비워도 남는다) → ③ 센터 비우기(자식 `Unloaded` → 워커·libvlc·구독 정리) 순서로 내린다. 이 순서를 지키지 않으면 죽은 자식의 버튼이 하단 바에 남거나(②를 빼먹음) 소리·파일 핸들이 그대로 남는다(③을 빼먹음).

## 10. 브랜드 에셋 단계형 적용 (A79, v0.119.0)

원칙: **브랜드 장식이 들어가는 자리를 목록 하나로 모으고, 각 자리가 "몇 레벨부터 켜지는가"를 표 하나로 정한다.** 적용 지점 코드는 레벨 숫자를 절대 비교하지 않고 `IsEnabled(BrandPoint)` 하나만 물어본다 — 지점별 하드코딩이 퍼지면 레벨 값 하나로 일괄 전환한다는 목적 자체가 깨진다.

```
settings.json  branding.assetLevel (int, 기본 0, 0~3 클램프)
      │  시작 후 1회 읽어 캐시 (실시간 반영 없음 — 고치면 재시작)
      ▼
BrandAssets.Level ──► MinimumLevel(BrandPoint) 표 ──► IsEnabled(point)
                                                        │
   ①NeutralPaw ②ModulePawMark(1) · ③Wordmark ⑤PawSpinner(2) · ④Mascot ⑥SiteLogo(3)
                                                        │
        BrandIcons(①②·GDI+ 벡터) · BrandAssets.CreateWordmark/CreateMascot(③④·래스터 조각)
        BrandSpinner(⑤·조각 회전) · site/index.html(⑥·정적, 자동 전환 불가)
```

- **런타임이 닿는 곳 / 닿지 않는 곳**: 창·작업표시줄·트레이 아이콘은 원본 `.ico` 위에 GDI+로 표식을 덧그려 레벨을 즉시 반영한다. 반면 **커밋되는 산출물**(탐색기 파일 아이콘 `Assets/fileicons/*.ico`, 설치 스플래시 `packaging/splash.png`, `site/`)은 레벨 값이 닿지 않는다 — 생성 스크립트를 레벨 인자로 다시 돌려 커밋하거나 HTML을 한 줄 바꿔야 한다. **커밋되는 산출물은 언제나 레벨 0으로 만든 것**이어야 한다.
- **표가 두 벌인 이유**: 런타임 표는 `src/KOTU.App/BrandAssets.cs`, 생성 스크립트 표는 `packaging/brand.py`. 언어가 달라 합칠 수 없고, 발바닥 도형(`BrandPaw.Shape` ↔ `brand.PAW`)도 같은 이유로 두 벌이다. **한쪽을 고치면 다른 쪽도 고친다**(서로를 주석으로 가리키고 있다).
- **왜 발바닥만 벡터인가**: ①②는 16px에서 읽혀야 한다(A46이 "KO/TU" 2줄을 고른 바로 그 이유). 시트에서 잘라낸 래스터를 16px로 줄이면 뭉개지므로 아이콘용 발바닥만은 양쪽 다 코드로 그린다. 워드마크·마스코트처럼 큰 자리만 래스터 조각을 쓴다.
- **실패는 조용히**: 에셋이 없거나 깨졌으면 장식을 생략하고 레벨 0의 모습으로 간다(SponsorAds의 sponsors.json 로드와 같은 규칙). 브랜드 장식 때문에 앱이 죽어서는 안 된다.

## 11. UI 매끄러움 최우선 원칙 (A178, 2026-08-20)

사용자 원문(2026-08-19, 전 항목의 상위 원칙):

> 모든 것보다 가장 중요한 것은 UI의 매끄러움. 데이터 로딩 지연·어떤 부하가 있어도 UI는
> 스레드적으로 완전히 독립되어 사용자가 '버벅인다'는 느낌을 전혀 받을 수 없어야 한다.
> UI/비UI가 스레드로 구분되어야 하고, 비UI는 반드시 단일 스레드일 필요 없다(멀티 허용).
> 설계적으로도 UI/비UI 구분·모듈별 구분·공통/개별 요소 구분이 확실해야 한다.

이 장 = **"무엇을 UI 스레드에 올리면 안 되는가"와 설계 경계의 정본.** "누가 어디서 도는가"는
8장(스레드 모델), 통합 모듈의 워커 수명은 9장이 정본이다 — 여기 중복하지 않는다.
전역 감사(2026-08-20) 결과의 감사표는 `docs/REQUIREMENTS.md` A178 항목 계보를 따른다.

### 11.1 UI 스레드 금지 목록

UI 스레드(디스패처)에서 발견되면 즉시 수리 대상:

1. **동기 파일·레지스트리·COM·WMI IO.** 예외 = 단일 메타데이터 조작(`File.Exists` 가드,
   이름변경·폴더 생성급 — `ExplorerFileOps.Rename` 선례)뿐이며, 예외를 쓰면 근거를 주석으로 남긴다.
2. **입력 크기에 상한이 없는 루프·대형 문자열 처리.** 상한 없는 입력(폴더 항목 수·파일 크기·
   아카이브 항목 수)을 도는 루프는 상한(Take/Limit — `TreeChildLimit`·`ThumbnailLimit` 선례)·
   분할·워커행 중 하나를 반드시 고른다. 임계 감각 = A177(문서 1M자).
3. **무거운 컨트롤 대입.** 대형 `Text=` 대입은 로딩 표시 프레임이 제출된 뒤로 지연(A177 ⓐ 선례),
   대량 `Children`/`Items` 조작은 상한·가상화(데이터 항목 + 템플릿)·증분 중 하나를 고른다.
   컨테이너(ListViewItem 등)를 항목 수만큼 직접 생성하는 것은 가상화를 무력화한다.
4. **`.Wait()`/`.Result`/`GetAwaiter().GetResult()` 금지.** 예외 = 전용 워커 스레드 위에서
   WinRT 비동기를 동기 대기하는 것(`ReadImageFile` 선례 — "전용 스레드라 UI 교착 없음"을
   주석으로 명시)과 UI가 아직 없는 프로세스 초입(단일 인스턴스 리디렉트)뿐.

### 11.2 워커 규칙

- 장기 작업은 `KOTU.Core.Threading` 계약(ModuleWorker/PollingWorker/WorkContext) 경유(8.1).
  임시 `Task.Run`은 짧고 UI 상태를 만지지 않는 일회성에만(`FileListOverlay.LoadChildrenAsync`·
  `ExplorerFileOps.TransferAsync` 선례) — 새로 쓰면 이유를 주석으로.
- 워커 → UI 복귀는 await 복귀 또는 `DispatcherQueue.TryEnqueue`만. 워커에서 UI 요소·셸 통지
  직접 접근 금지(트레이 통지도 UI 스레드에서 — HardwareView A101 선례).
- 배경 폴링·유지보수는 BelowNormal(8.4) — 재생·입력과 CPU를 다투지 않는다. 시작 시
  레지스트리 유지보수(App.ShellRegistrationMaintenance)가 선례.
- 결과 반영 전 낡음 검사(seq 가드)·취소(`CancellationToken`)·뷰 Unloaded 정리(8.4 수명)를 갖춘다.

### 11.3 설계 경계 규칙

- **의존 방향**: 모듈 → Core만. 모듈은 셸(KOTU.App)·타 모듈을 참조하지 않는다(모듈 간 통신 =
  Core의 이벤트/라우터/계약 경유). 셸만 모듈을 참조한다. csproj `ProjectReference`가 정본이다.
- **셸 기능이 모듈에 필요하면** Core에 훅(정적 델리게이트)을 두고 셸이 구현을 꽂는다
  (`DesktopWallpaperHook`·`AdminRelaunchHook`·`DefaultAudioInputHook` 선례).
- **DllImport·커스텀 COM interop은 셸(KOTU.App)에만 — 모듈 0건 규약.** WinRT 프로젝션 헬퍼
  (`WinRT.Interop.InitializeWithWindow`·`Win32Interop.GetWindowFromWindowId`)는 SDK 표면이라 예외.
- **공통/개별**: 두 모듈 이상이 쓰는 표면은 공용(Core 계약 + 셸 공용 컨트롤·오버레이)으로 올리고,
  모듈은 계약으로 자리를 내준다(`IDriveStripHost`·`DriveStrip` 선례).

### 11.4 비UI 병렬 허용 조건

비UI는 단일 스레드일 필요 없다(원문). 단, 병렬화 전에 아래를 판정한다:

1. **순서 보장이 사양인 곳은 직렬 유지** — 같은 파일 상태를 다루는 큐(문서 읽기/저장),
   아카이브 핸들 하나(SevenZip 인스턴스는 비스레드세이프), 이동/복사(충돌 대화상자 흐름·수행분
   유지가 순서 의미를 가짐).
2. **공유 자원 직렬 규약은 병렬화 금지** — LHM(커널 드라이버)·WMI 수집 = hardware poller 한 곳
   (8.4), 레지스트리 Capabilities = settings worker 한 곳(8.2 — 동시 쓰기 위험).
3. **독립 항목 다건은 상한 있는 동시성으로 병렬 허용** — 항목별 완료 반영이라 순서 무관인 것
   (썸네일·상세 조각·선읽기)만, 동시성 상한(예: 2~4)을 두고. 취소·낡은 결과 폐기(seq)는 직렬
   때와 동일하게 갖춘다.

### 11.5 새 코드 체크리스트

- [ ] 이 코드는 UI 스레드에서 도는가? 돈다면 11.1 금지 목록(동기 IO·무상한 루프·무거운 대입·동기 대기)에 걸리지 않는가?
- [ ] 입력 크기(항목 수·바이트)에 상한이 있는가? 없으면 상한·분할·워커행 중 무엇을 골랐는가?
- [ ] 장기 작업이 Core 스레딩 계약을 타는가? 임시 Task.Run이면 근거 주석이 있는가?
- [ ] seq 가드·취소·Unloaded 정리(8.4)가 있는가?
- [ ] 새 참조가 의존 방향(11.3)을 지키는가? 모듈에 DllImport/COM을 넣지 않았는가?
- [ ] 병렬화했다면 11.4의 직렬 사양·공유 자원 규약을 침범하지 않는가?
