# WinUtil

윈도우 필수 유틸(압축·이미지 뷰어·동영상 플레이어, 추후 하드웨어 정보/모니터링/스트레스 테스트)을 하나로 통합한 앱. 설계는 [ARCHITECTURE.md](ARCHITECTURE.md) 참고.

## 빌드 (Windows 필요)

```
dotnet test                              # Core 단위 테스트
dotnet build src/WinUtil.App -p:Platform=x64   # WinUI 3 앱 (Visual Studio 2022 + Windows App SDK 워크로드 권장)
```

## 상태

- v0.2.0 — Phase 0: 셸 + Core 계약 (IModule, 파일 라우터, 설정, 단일 인스턴스)
