# WinUtil

> 윈도우 새로 깔면 제일 먼저 설치하던 필수 유틸들 — 압축, 이미지 뷰어, 동영상 플레이어 — 를 하나의 앱으로.

![build](https://github.com/tsusaikang/winutil/actions/workflows/build.yml/badge.svg)
![Platform](https://img.shields.io/badge/platform-Windows%2010%2B%20(x64)-0078d4)
![.NET](https://img.shields.io/badge/.NET-8.0-512bd4)
![UI](https://img.shields.io/badge/UI-WinUI%203-41b883)
![License](https://img.shields.io/badge/license-MIT-green)

파일을 더블클릭하면 확장자에 맞는 기능이 하나의 창에서 열립니다. 압축 파일이면 내부 탐색, 이미지면 뷰어, 동영상이면 플레이어. 각 기능은 검증된 엔진(7-Zip, libvlc 등)을 통합하고, WinUtil은 하나로 묶인 경험에 집중합니다.

## 기능

| 모듈 | 상태 | 주요 기능 |
|---|---|---|
| 압축 | ✅ v0.4.0 | zip·7z·rar·tar·gz 해제 / zip·7z 생성(암호 지원) / 풀지 않고 내부 탐색 / 드래그&드롭 압축 / 한글 파일명(CP949) 자동 복구 |
| 이미지 뷰어 | ✅ v0.3.0 | ←/→ 폴더 탐색(자연 정렬) / 줌·팬 / 회전 / 전체화면 / 휴지통 삭제 / EXIF 회전 / GIF 애니메이션 |
| 동영상 플레이어 | ✅ v0.5.0 | libvlc 기반 재생(코덱 설치 불필요) / 시킹·볼륨·배속 / 자막 자동 탐지(srt·smi, CP949 자동 변환) / 이어보기 / 전체화면 |
| 탐색기 통합 | ✅ v0.8.0 | 파일 연결 등록(모듈별, 현재 사용자 범위) / 우클릭 "여기에 풀기"·"WinUtil로 압축" / 설정에서 켜고 끔 |
| 설치·자동 업데이트 | ✅ v0.9.0 | Setup.exe 한 파일 설치(Velopack) / GitHub Releases 피드로 시작 시 업데이트 확인·원클릭 적용 |
| 하드웨어 | 🚧 v0.10.0 | 스펙 표시(CPU·메인보드·메모리·그래픽·저장장치, 전체 복사) — 센서 모니터링·스트레스는 예정 |

**다운로드**: [Releases](https://github.com/tsusaikang/winutil/releases/latest)에서 —

- **`WinUtil-win-Setup.exe`** — 설치판. 시작 메뉴 등록, 자동 업데이트. 사용자 범위 설치라 관리자 권한 불필요.
- **`WinUtil-win-Portable.zip`** — 무설치판. 아무 폴더에나 풀어 실행하면 되고, 역시 자동 업데이트됩니다.

어느 쪽이든 압축(7z.dll)·동영상(libvlc) 엔진이 전부 동봉되어 바로 동작합니다. 그 외 릴리스 파일(nupkg 등)은 업데이트 시스템 내부용이니 받지 않아도 됩니다.

> ⚠️ 개발 초기 단계입니다. CI(Windows)에서 빌드·단위 테스트는 통과했지만 실사용 검증은 진행 중입니다.

## 소스에서 빌드하기

요약 (자세한 안내는 **[docs/BUILD.md](docs/BUILD.md)**):

```powershell
git clone https://github.com/tsusaikang/winutil.git
cd winutil
dotnet test                                     # Core·모듈 로직 단위 테스트
dotnet build src/WinUtil.App -p:Platform=x64    # 앱 빌드
copy "C:\Program Files\7-Zip\7z.dll" src\WinUtil.App\bin\x64\Debug\net8.0-windows10.0.19041.0\  # 압축 기능용
```

필요 환경: Windows 10 1809+ (x64), .NET 8 SDK, Visual Studio 2022(또는 Build Tools) + "WinUI 애플리케이션 개발" 워크로드.

## 프로젝트 구조

```
src/WinUtil.Core        # 모듈 계약(IModule)·파일 라우터·설정 — UI 비의존
src/WinUtil.App         # WinUI 3 셸: 단일 인스턴스, 파일 → 모듈 라우팅
src/WinUtil.Module.*    # 기능 모듈 (압축, 이미지, ...)
tests/                  # 단위 테스트 (xunit)
```

설계 배경과 로드맵은 [ARCHITECTURE.md](ARCHITECTURE.md) 참고.

## 버전 이력

- v0.10.0 — 하드웨어 정보 모듈 (WMI 스펙 표시)
- v0.9.x — Setup.exe 설치본·자동 업데이트 (Velopack, 진행률·주기 확인)
- v0.8.0 — 탐색기 통합 (파일 연결·우클릭 메뉴·설정 페이지)
- v0.7.x — 실사용 다듬기 (풀기 충돌 처리·스마트 해제·창 전체 드롭·플레이어 관례 조작)
- v0.6.x — 배포 안정화 (열기 버튼·드래그&드롭·resources.pri·릴리스 자동화)
- v0.5.0 — 동영상 플레이어 모듈 (libvlc 재생·시킹·배속·자막 CP949 변환·이어보기)
- v0.4.0 — 압축 모듈 (해제·생성·내부 탐색·암호·CP949 대응)
- v0.3.0 — 이미지 뷰어 모듈 (방향키 탐색·줌·회전·전체화면·휴지통 삭제)
- v0.2.0 — 셸 + Core 계약 (IModule, 파일 라우터, 설정, 단일 인스턴스)
- v0.1.0 — 아키텍처 설계

## 라이선스

WinUtil 자체 코드는 [MIT](LICENSE)입니다. 함께 배포·사용되는 외부 구성요소는 각자의 라이선스를 따릅니다 — [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) 참고 (7-Zip: LGPL, libvlc: LGPL, LibreHardwareMonitor: MPL-2.0 등).
