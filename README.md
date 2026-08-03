# WinUtil

윈도우 필수 유틸(압축·이미지 뷰어·동영상 플레이어, 추후 하드웨어 정보/모니터링/스트레스 테스트)을 하나로 통합한 앱. 설계는 [ARCHITECTURE.md](ARCHITECTURE.md) 참고.

## 빌드 (Windows 필요)

```
dotnet test                              # Core 단위 테스트
dotnet build src/WinUtil.App -p:Platform=x64   # WinUI 3 앱 (Visual Studio 2022 + Windows App SDK 워크로드 권장)
```

## 상태

- v0.2.0 — Phase 0: 셸 + Core 계약 (IModule, 파일 라우터, 설정, 단일 인스턴스)
- v0.3.0 — Phase 1: 이미지 뷰어 모듈 (방향키 탐색·줌·회전·전체화면·휴지통 삭제)
- v0.4.0 — Phase 2: 압축 모듈 (해제·생성·내부 탐색·암호·CP949 대응)

압축 모듈은 LGPL인 7-Zip의 7z.dll(x64)을 동적 로드한다 — 7-Zip 설치본(`C:\Program Files\7-Zip\7z.dll`) 또는 7z extra에서 복사해 실행 폴더에 두거나, `src/WinUtil.Module.Archive/runtimes/win-x64/`에 두면 빌드 시 자동 복사된다.
