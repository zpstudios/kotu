# WinUtil

> 윈도우 새로 깔면 제일 먼저 설치하던 필수 유틸들 — 압축, 이미지 뷰어, 동영상 플레이어 — 를 하나의 앱으로.

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
| 동영상 플레이어 | 🚧 예정 | libvlc 기반 재생 / 자막(srt·smi) / 이어보기 / 배속 |
| 하드웨어 정보·모니터링 | 📋 계획 | CPU-Z류 스펙 표시 / 센서 모니터링 / 스트레스 테스트 |

> ⚠️ 현재 개발 초기 단계입니다. Windows 실빌드 검증 전이며, 바이너리 릴리스는 아직 없습니다.

## 소스에서 빌드하기

요약 (자세한 안내는 **[docs/BUILD.md](docs/BUILD.md)**):

```powershell
git clone https://github.com/<owner>/winutil.git
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

- v0.4.0 — 압축 모듈 (해제·생성·내부 탐색·암호·CP949 대응)
- v0.3.0 — 이미지 뷰어 모듈 (방향키 탐색·줌·회전·전체화면·휴지통 삭제)
- v0.2.0 — 셸 + Core 계약 (IModule, 파일 라우터, 설정, 단일 인스턴스)
- v0.1.0 — 아키텍처 설계

## 라이선스

WinUtil 자체 코드는 [MIT](LICENSE)입니다. 함께 배포·사용되는 외부 구성요소는 각자의 라이선스를 따릅니다 — [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) 참고 (7-Zip: LGPL, libvlc: LGPL, LibreHardwareMonitor: MPL-2.0 등).
