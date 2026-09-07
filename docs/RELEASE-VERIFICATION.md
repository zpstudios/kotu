# 구조 개선 3단계: 정식 배포 검증 필수화

> **현재 상태 · 2026-09-08**: 사용자가 “별 문제 없이 잘 되는 것 같네”라고 정상 사용 피드백을
> 전하고 기본 브랜치 병합을 승인했다. 이는 개별 UI·클라우드·재생 시험 항목을 모두 수행했다는
> 보고가 아니다. 1~5단계를 v0.357.0으로 master에 병합·푸시했다.
> 병합 커밋: `669b865a1a26ebe64cc02fdb18450e2d2a4d2b4f`.
> [정식 v0.357.0](https://github.com/zpstudios/kotu/releases/tag/v0.357.0)을
> 2026-09-08 00:03:04 KST에 발행했다(초안·사전 공개 아님). 태그는 위 병합 커밋과 일치한다.
> [master build 34135688204](https://github.com/zpstudios/kotu/actions/runs/34135688204)와
> [release 34135688114](https://github.com/zpstudios/kotu/actions/runs/34135688114) 모두 성공했다.
> Setup·Portable·full 패키지·업데이트 피드와 실제 delta 패키지(666,495 bytes)가 게시됐다.
> 델타 생성은 검증했으며, 기존 설치본에 실제 업데이트를 적용하고 설정 보존을 확인하는 시험은 미실행이다.
> 아래 작업 브랜치 및 병합 전 검증 이력은 유지한다. 각 단계에 명시한 기술적 한계는 그대로다.

2026-09-07 · v0.355.0 · A358 · `codex/architecture-document-session`

사용자가 3~5단계의 직렬 진행을 지시했다. 이번 단계는 배포 파이프라인만 변경하며 앱 동작은
변경하지 않는다. 당시 단계 범위는 브랜치 검증까지였으며, 현재 병합·배포 상태는 위 기록을 따른다.

## 배포 계약

`release.yml`의 한 작업 안에서 구조 검사 → 전체 솔루션 복원·Release/x64 빌드·테스트 →
앱 publish → 엔진·리소스·시작 검사 → Velopack 패키징 → 피드·실제 설치·설치 파일 해시·
설치본 시작 검사를 순서대로 수행한다. 모든 단계 성공 뒤에만 `master`의 최종 Release
액션이 실행된다. 테스트 결과는 실패해도 별도 아티팩트로 보관한다.

`build.yml`과 정식 배포는 `eng/Prepare-Package.ps1`, `Test-AppStartup.ps1`,
`Test-Installer.ps1`을 공유한다. 필수 파일은 문서 모델·파일 전송 DLL, PRI, 아이콘,
샘플 미디어, 7-Zip, VLC와 라이선스 고지다. 설치 검사는 기존 Velopack 1.2.0 기본 제외
진단 파일 외 모든 publish 파일의 SHA-256을 대조한다. 로컬 설치 호출은 거절한다.

`Test-ReleasePackage.ps1`은 Setup·Portable·피드의 존재 및 현재 버전 full 패키지 항목을
확인하고 피드에 기재된 현재 버전 자산의 크기·SHA-1·SHA-256(제공된 경우)을 검사한다.
피드 계약은 [Velopack 1.2.0 VelopackAsset](https://github.com/velopack/velopack/blob/1.2.0/src/lib-csharp/VelopackAsset.cs)을 따른다.

고정 concurrency 그룹 `release`, 실행 중 취소 금지, 이미 있는 버전 태그의 master 발행
건너뛰기를 유지한다. 델타 베이스는 master에서만 취득한다. 다운로드는 별도 `vpk_base`로
격리하고 성공한 full 패키지만 pack 입력으로 복사한다. 실패 시 재귀 삭제 없이 풀 패키지
발행으로 계속한다. 기존 패키징의 델타 생성 및 이전 버전 nupkg 업로드 제외를 유지한다.

## 브랜치 검증과 현재 상태

`release`를 작업 브랜치에서 수동 실행하면 같은 빌드·패키징·설치 검사를 수행하고
`KOTU-release-dry-run-<SHA>` 아티팩트만 남긴다. 태그·Release 생성 액션은 master 조건으로
차단한다. 이미 있는 버전 태그도 브랜치 시험은 막지 않는다. 직전 정식 배포의 다운로드는
생략하므로 이 시험은 새 풀 패키지 경로를 검증하며 실제 델타 생성은 검증하지 않는다.

- `eng/Test-ReleaseWorkflow.ps1`: 단일 작업에서 필수 검사의 순서·실패 전파·최종 master
  조건·멱등·직렬화를 정적 검사한다. master 조건 제거, 일부 프로젝트만 테스트,
  테스트 오류 무시, 설치 검사 제거의 네 변형을 모두 거절했다.
- 공식 배포본 actionlint 1.7.7로 전체 워크플로 문법 검사 통과. 공통 PowerShell 파싱 통과.
- 피드 fixture 정상 통과, 잘못된 버전·해시·상위 경로 파일명 및 로컬 설치 호출 거절 확인.
- 로컬 .NET SDK 8.0.424에서 전체 솔루션 Release/x64 빌드 성공(경고 0·오류 0), 8개 테스트 프로젝트 285건 통과(실패/건너뜀 0). 구조 검사 19개 프로젝트 통과. 테스트는 Windows 파일 핸들·junction 검사를 위해 샌드박스 밖에서 실행했다. 결과는 무시 대상 `TestResults/phase3`에 보관한다.
- [GitHub build 34128976408](https://github.com/zpstudios/kotu/actions/runs/34128976408) 및
  [브랜치 release dry run 34129019513](https://github.com/zpstudios/kotu/actions/runs/34129019513) 모두 성공.
  검증 소스는 `042caab178b749315c43a267604e26ef5961310c`다. 전체 빌드·테스트·publish·
  엔진/리소스·시작 검사, Setup/Portable/풀 패키지 생성, 업데이트 피드의 버전·크기·해시 검사,
  실제 설치·설치 파일 SHA-256 일치·설치본 시작 및 시험 아티팩트 보관까지 통과했다.
  태그·정식 Release는 발행하지 않았다. 직전 정식 배포 다운로드를 건너뛴 브랜치 시험이므로
  이전 버전을 바탕으로 한 델타 생성 경로는 이번 실행에서 검증하지 않았다.
- 5단계까지 반영한 v0.357.0 최종 소스 `13380ab505d10a2d2b2918b3fdd84915f569aac3`에서도
  [build 34131850705](https://github.com/zpstudios/kotu/actions/runs/34131850705)와
  [release dry run 34131883483](https://github.com/zpstudios/kotu/actions/runs/34131883483)이 모두 성공했다.
  전체 빌드·테스트·publish·엔진/리소스·시작 검사 및 Setup/Portable/풀 패키지·피드 검사,
  실제 설치·설치 파일 해시·설치본 시작·시험 아티팩트 보관이 통과했다.
  [시험 배포물 아티팩트](https://github.com/zpstudios/kotu/actions/runs/34131883483/artifacts/10022607596)는
  `KOTU-release-dry-run-13380ab505d10a2d2b2918b3fdd84915f569aac3`다.
  태그·Release 업로드와 직전 릴리스 취득은 실제 실행에서도 skipped를 확인했다.
  이 결과는 브랜치 풀 패키지 검증이며 공식 발행이나 델타 생성 성공을 뜻하지 않는다.

앱 UI의 모든 조작, 실제 사용자 컴퓨터의 설치 환경, 전원 장애나 서버 장애까지 검증하는
단계는 아니다. 시작 검사는 프로세스 생존과 시작 오류 로그를 확인한다.

## 다섯 단계 진행 순서

1. A356 완료: 문서 저장 세션·파일 서비스 분리. `ARCHITECTURE-MIGRATION.md`.
2. A357 완료: 파일 복사·이동의 계획/실행/원본 정리 분리. `FILE-TRANSFER-MIGRATION.md`.
3. A358 완료(브랜치): 정식 배포의 구조·빌드·테스트·패키지 검사 필수화 및 GitHub 시험 배포 검증.
4. A359 완료(브랜치): 콘텐츠 계약 수명·All Readable 중계·늦은 결과와 닫기 승인 검증.
   `CONTENT-LIFETIME-MIGRATION.md`.
5. A360 완료(브랜치): 제한 시간 대기·썸네일 배치 취소·미디어 시간 표시와 이어보기 정책 분리.
   `ASYNC-MEDIA-MIGRATION.md`. 협조하지 않는 COM 작업의 강제 종료나 네이티브 재생기
   워커 전면 개편은 포함하지 않는다.

병합 전 이력: 다섯 단계 모두 검수·커밋·작업 브랜치 push·GitHub 검증을 완료했다. 당시 기본 브랜치 병합과
정식 Release 발행은 하지 않았으며 실기기 수동 확인 및 각 단계에 명시한 한계는 남는다.
