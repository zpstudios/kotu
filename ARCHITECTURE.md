# WinUtil — 올인원 Windows 유틸리티 아키텍처 설계

버전: v0.1.0 (설계 초안) · 2026-08-03

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
| 스트레스 테스트 | 자체 구현(정수/부동소수 연산, 메모리 대역폭) | 단순 부하 생성은 자체 구현이 적합 |
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
├──────────┬──────────┬──────────┬────────────┤
│ Archive  │ Image    │ Video    │ Hardware   │
│ Module   │ Viewer   │ Player   │ (Phase 5)  │
│          │ Module   │ Module   │ Info·Mon·  │
│          │          │          │ Stress     │
└──────────┴──────────┴──────────┴────────────┘
```

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

### 솔루션 구성(예정)

```
WinUtil.sln
├─ src/WinUtil.App          # 셸 (실행 파일)
├─ src/WinUtil.Core         # 계약(인터페이스) + 공통 서비스
├─ src/WinUtil.Module.Archive
├─ src/WinUtil.Module.Image
├─ src/WinUtil.Module.Video
├─ src/WinUtil.Module.Hardware   # Phase 5
└─ tests/                   # 모듈별 단위 테스트
```

## 4. 모듈별 기능 명세 (필수 / 옵션)

### 4.1 압축 (Archive)

- 필수: zip·7z·rar·tar·gz 해제 / zip·7z 생성 / 압축 내부 탐색(풀지 않고 미리보기) / 드래그&드롭 / 탐색기 컨텍스트 메뉴("여기에 풀기", "압축하기") / 암호 걸린 파일 처리 / 한글 인코딩(CP949↔UTF-8) 자동 처리
- 옵션: 분할 압축, 압축률 선택, 압축 내부 파일 즉시 열기(이미지/영상 모듈로 라우팅), 무결성 검사

### 4.2 이미지 뷰어 (Image)

- 필수: jpg·png·gif(애니)·bmp·webp 표시 / ←→ 키로 폴더 내 이전·다음 / 확대·축소(휠)·패닝·창맞춤 / 회전 / 전체화면 / 삭제(휴지통) / EXIF 회전 반영
- 옵션: heic·psd·raw(Magick.NET), 슬라이드쇼, EXIF 정보 패널, 간단 편집(크롭·리사이즈), 배경화면 설정

### 4.3 동영상 플레이어 (Video)

- 필수: mp4·mkv·avi·webm 등 재생(libvlc가 커버) / 재생·일시정지·시킹 / 볼륨·배속 / 자막(srt·smi, 한글 인코딩 처리) / 전체화면 / 이어보기(마지막 위치 기억) / ←→ 키 탐색(초 단위 점프)
- 옵션: 자막 싱크·폰트 조절, 오디오 트랙 선택, 스크린샷, 구간 반복, 재생목록

### 4.4 하드웨어 (Phase 5)

- 정보(CPU-Z류): CPU·메인보드·RAM·GPU·스토리지 스펙 표시
- 모니터(HWMonitor류): 온도·클럭·팬·전압·사용률 실시간 그래프
- 스트레스: CPU 전코어 부하, 메모리 부하, 모니터링과 연동한 안정성 테스트
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

## 6. 주요 리스크

1. WinUI 3 + LibVLCSharp 조합 — VideoView 지원이 WPF 대비 늦다. Phase 3 착수 전 스파이크(1일 검증) 필수. 문제 시 SwapChainPanel 직접 렌더 또는 Flyleaf(ffmpeg 기반) 대체.
2. 네이티브 dll 배포 — 7z.dll, libvlc(약 80MB)로 배포 용량 증가. x64 전용으로 시작해 단순화.
3. 하드웨어 센서 권한 — 커널 드라이버 서명·로드 문제. Phase 5로 미룬 이유이며, unpackaged 배포 선택의 근거.
4. 탐색기 컨텍스트 메뉴 — Windows 11 새 메뉴는 packaged 등록이 유리해 unpackaged와 상충. Phase 4에서 하이브리드(sparse package) 검토.

## 7. 다음 단계

1. 이 설계 검토·확정
2. Phase 0 착수: 솔루션 뼈대 + Core 계약
3. 스파이크 2건 선행: WinUI3+LibVLCSharp 재생 검증, 7z.dll 래퍼 후보 비교
