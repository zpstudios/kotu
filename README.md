# KOTU

> 윈도우 새로 깔면 제일 먼저 설치하던 필수 유틸들 — 사진·동영상·음악·문서·압축 — 을 하나의 앱으로.

![release](https://github.com/zpstudios/kotu/actions/workflows/release.yml/badge.svg)
![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11%20(x64)-0078d4)
![.NET](https://img.shields.io/badge/.NET-8.0-512bd4)
![UI](https://img.shields.io/badge/UI-WinUI%203-41b883)
![License](https://img.shields.io/badge/license-MIT-green)

파일을 더블클릭하면 확장자에 맞는 모듈이 **같은 창**에서 열립니다. 사진이면 뷰어, 동영상이면 플레이어,
PDF면 문서 뷰어, 압축 파일이면 내부 탐색. 창의 껍데기(사이드 패널·하단 바·트레이 아이콘)는 그대로 있고
가운데 화면과 하단 바만 바뀝니다. 재생·해제 같은 무거운 일은 검증된 엔진(7-Zip, libvlc,
LibreHardwareMonitor)에 맡기고, KOTU는 **하나로 묶인 경험**에 집중합니다. 필요한 엔진은 전부 동봉되어
있어 코덱 팩이나 별도 설치가 필요 없습니다.

## 기능

일곱 개 모듈을 하단 왼쪽 메뉴에서 전환합니다.

| 모듈 | 상태 | 주요 기능 |
|---|---|---|
| **All Readable** | ✅ | 다른 모듈이 여는 모든 형식을 한 모듈에서 열기 (파일 연결은 잡지 않음 — 탐색기 더블클릭은 전용 모듈로) |
| **이미지** (Image) | ✅ | jpg·png·gif·webp·bmp·tif·ico·psd / ←→ 폴더 탐색(자연 정렬) / 휠 줌 10~800%·드래그 팬 / 회전(EXIF 자동 적용) / GIF 애니메이션 / 휴지통 삭제 / 인쇄 / EXIF 요약 표시 |
| **비디오** (Video) | ✅ | mp4·mkv·avi·webm·mov·wmv 등 13종 / libvlc 내장 재생 / 시킹·볼륨·배속(0.5~2×) / 자막 자동 탐지(srt·smi·ass 등, CP949 자동 변환) / 이어보기 / 폴더 연속 재생 + 반복 모드 / Ctrl+휠 줌 / 재생 중 하단 바 자동 숨김 |
| **오디오** (Audio) | ✅ | mp3·flac·wav·ogg·opus·m4a·aac·wma / 실시간 비주얼라이저 4종(Scope·Spectrum·Spectrometer·VU) / 출력 장치 선택·이퀄라이저 / 이어보기 / 폴더 연속 재생 + 반복 모드 |
| **문서** (Document) | ✅ | txt·md·html·log·ini 편집 + pdf 보기 / 편집 ↔ 보기 모드 전환 / 마크다운·HTML 렌더 뷰 / 인쇄(PDF는 페이지 범위 지정) / 인코딩(UTF-8·UTF-16·CP949)·줄바꿈 유지 저장, 저장 전 경고·저장 후 검증 / PDF 연속 스크롤 / Ctrl+휠 20~500% 확대 |
| **압축** (Archive) | ✅ | zip·7z·rar·tar·gz·tgz·bz2·xz 해제 / zip·7z 생성(암호 지원, 7z은 파일명까지 암호화) / 풀지 않고 내부 탐색·개별 파일 열기 / 드래그&드롭 압축 / 한글 파일명(CP949) 자동 복구 / 진행률·취소 |
| **H/W Info** | ✅ | 센서 타일 10종(CPU·GPU 온도/전력/부하/클럭, RAM, 팬, SSD 온도) 실시간 그래프 / 사양 목록(CPU·GPU·RAM·메인보드·저장장치·네트워크·시스템) 전체 복사 / 갱신 주기 선택 / 항상 위 / 선택 센서를 트레이 아이콘에 표시 |

> CPU 온도·전력, 팬 속도, 드라이브 온도는 관리자 권한(과 PawnIO 드라이버)이 필요합니다. 읽을 수 없을 때는
> 화면에서 이유와 함께 **관리자로 재시작** 또는 드라이버 안내를 제공합니다.

**모든 모듈이 공유하는 것**

- **내장 파일 브라우저** — 폴더 트리·파일 목록·썸네일 격자, 복사/이동/이름 변경/삭제(휴지통), 이름 충돌
  처리(덮어쓰기·둘 다 유지·건너뛰기), 정렬·필터, 탐색기와 오가는 드래그&드롭.
- **좌우 사이드 패널** — `F11`(폴더·파일 목록) / `F12`(파일 정보). 전체화면은 `Enter` 또는 `Alt+Enter`.
- **여러 창** — 한 프로그램에 창을 원하는 만큼(`Shift+N`). 창마다 번호·모듈 색·자체 트레이 아이콘.
- **탐색기 통합** — 모듈별 파일 연결 등록, 우클릭 "여기에 풀기"·"압축". 전부 **현재 사용자 범위**라
  관리자 권한이 필요 없고, 스위치를 끄면 흔적 없이 해제됩니다.
- **UI 배율** — 시스템 기본 또는 100~350% 고정(KOTU 창에만 적용).

자세한 조작법과 단축키 전체 표는 **[docs/USER-GUIDE.md](docs/USER-GUIDE.md)** — 웹 게시본은 `site/guide.html`.

## 다운로드

[**Releases**](https://github.com/zpstudios/kotu/releases/latest)에서 —

- **`KOTU-win-Setup.exe`** — 설치판. 시작 메뉴 등록, 자동 업데이트. 사용자 범위 설치라 관리자 권한 불필요.
- **`KOTU-win-Portable.zip`** — 무설치판. 아무 폴더에나 풀어 실행하면 되고, 역시 자동 업데이트됩니다.

어느 쪽이든 압축(7z.dll)·재생(libvlc) 엔진이 전부 동봉되어 받는 즉시 동작합니다. 그 외 릴리스 파일
(nupkg 등)은 업데이트 시스템 내부용이니 받지 않아도 됩니다. 업데이트는 설정 화면의 **Updates**에서
확인·적용합니다(다운로드 후 원클릭 재시작).

> **KOTU는 공식 빌드 바이너리(Releases)로만 배포·지원합니다.** 소스는 공개되어 있지만, 자체 빌드는
> 지원 대상이 아니며 빌드 방법도 안내하지 않습니다.

> 활발히 개발 중인 앱입니다. 모든 릴리스는 Windows CI에서 빌드·단위 테스트를 통과하며, 실사용 환경
> 검증은 계속 진행하고 있습니다. 문제는 [이슈](https://github.com/zpstudios/kotu/issues)로 알려 주세요.

**요구 사항**: Windows 10 버전 1809 이상 또는 Windows 11 (x64). HTML 렌더 뷰는 Windows 11에 기본 포함된
WebView2 런타임을 사용합니다(없으면 소스 보기로 대체).

## 프로젝트 구조

```
src/KOTU.Core        # 모듈 계약(IModule)·파일 라우터·설정 — UI 비의존
src/KOTU.App         # WinUI 3 셸: 단일 인스턴스, 다중 창, 파일 → 모듈 라우팅
src/KOTU.Module.*    # 기능 모듈 (AllReadable, Image, Video, Audio, Document, Archive, Hardware)
tests/               # 단위 테스트 (xunit)
```

설계 배경과 로드맵은 [ARCHITECTURE.md](ARCHITECTURE.md), 남은 작업 목록은
[docs/REQUIREMENTS.md](docs/REQUIREMENTS.md), 작업 이어받기용 요약은 [docs/HANDOVER.md](docs/HANDOVER.md).

> 이름 변경 이력: 초기 코드명 `WinUtil` → `ZP`(v0.33.0) → **KOTU (King Of The Util)**(v0.86.0~).
> 실행 파일은 `KOTU.exe`(v0.88.0~). 일부 내부 식별자는 구 버전 설치본 정리·자동 업데이트 호환을 위해
> 옛 이름을 그대로 유지합니다.

버전별 변경 사항은 [Releases](https://github.com/zpstudios/kotu/releases)의 릴리스 노트를 참고하세요.

## 라이선스

KOTU 자체 코드는 [MIT](LICENSE)입니다. 함께 배포·사용되는 외부 구성요소는 각자의 라이선스를 따릅니다 —
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) 참고 (7-Zip: LGPL, libvlc: LGPL,
LibreHardwareMonitor: MPL-2.0 등).
