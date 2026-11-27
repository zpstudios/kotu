# 작업 근거 회수 색인

현재 사양은 기능 문서, 현재 열린 상태는 [요구사항](../REQUIREMENTS.md), 현재 검증 경계는 [실기기 확인](../DEVICE-CHECKS.md)이 정본이다. 여기는 원문을 복사하지 않고 고유한 요구·정정·실패·보호 자료를 회수하는 공용 입구다. 향후 작업도 기존 주제/행을 갱신하며 작업별 장문 보관 문서를 추가하지 않는다.

## 고정 원문

| 회수할 내용 | 위치 |
|---|---|
| A418–A435 및 A112의 요구·당시 판단 | [결과 색인](requirements-completed-index.md)에 A번호별 정본·고정 행 링크 |
| v0.428.0–v0.433.9 제품 커밋·CI번호·배포/설치 결과·근거 파일 | [2026-11-25 HANDOVER](https://github.com/zpstudios/kotu/blob/67c4b1c834a15e45b53f8d5f8e29466dc5ce9172/docs/HANDOVER.md#L1-L38) |
| A437 v0.434.0 배포·설치와 창 설정 복원, A438 제품 체크포인트 | [A438 커밋 당시 HANDOVER](https://github.com/zpstudios/kotu/blob/6403859a1df482d3cc4a4852166d959fd1414bee/docs/HANDOVER.md#L5-L16). A437 완료를 다시 열지 않으며 A438의 현재 설치 상태는 최신 HANDOVER를 따름 |
| A438 v0.435.0 공식 배포·설치·설정 보존과 A439 시작 조건 | [A439 제품 커밋 당시 HANDOVER](https://github.com/zpstudios/kotu/blob/ab6e992c043ddc8f015b7b1fe1e85414c81e03cd/docs/HANDOVER.md#L5-L16). A438 공식 1,188파일·43키 대조와 당시 미관측 범위를 보존하며 현재 제품 상태는 최신 HANDOVER를 따름 |
| A439 v0.436.0 공식 배포·설치와 최대화 설정 복원 | [v0.437.0 제품 커밋 당시 HANDOVER](https://github.com/zpstudios/kotu/blob/f155a2b41a58155fe86487089dd6362f82878d36/docs/HANDOVER.md#L5-L8). v0.436.0 제품 커밋·CI·공식 1,188파일·43키 대조, 첫 재시작의 최대화 설정을 정상 UI로 복원한 경과와 당시 보호 경로를 보존 |
| A439 메뉴 판정 정정·도입 전 소스 재구성·UIA 캐시와 크기 변경 조사 | [기능 정본의 조건과 한계](../features/shell/design-library-A427.md#a439). `artifacts/a439-followup/`의 빌드/계측 manifest·normal-comparison·results·화면·nettrace와 캐시 0.25 단독 최대화 실패를 보존. 정상 검증은 `final-validation.json`·`final-normal-build.json`·`versioned-normal-build.json`·`final-normal-ui.json`·화면·`ui-restoration.json`, v0.437.0 배포/설치는 `workflows.json`·`official-release/verification.json`·`installed-release-verification.json`·`official-update-ui.json`으로 회수. 앱 준비 ZIP과 공개 Full의 표현 차이·항목 바이트 일치는 `app-update-package-comparison.json`에 보존 |
| A427–A435 화면 조건·측정·복원 차이·실패·미관측 | [당시 실기기 원문](https://github.com/zpstudios/kotu/blob/67c4b1c834a15e45b53f8d5f8e29466dc5ce9172/docs/DEVICE-CHECKS.md#L54-L65). 현재 남은 조건은 실기기 표에서만 갱신 |
| A433 자동 열 감소 해석·설치 요청 철회와 고정 열 정정 | [요구와 사용자 정정](https://github.com/zpstudios/kotu/blob/67c4b1c834a15e45b53f8d5f8e29466dc5ce9172/docs/REQUIREMENTS.md#L16) · [철회 검수](https://github.com/zpstudios/kotu/blob/67c4b1c834a15e45b53f8d5f8e29466dc5ce9172/docs/DEVICE-CHECKS.md#L56-L57) · [정정 사양](../features/explorer/tile-captions-A387.md#a433-고정-열-수와-조작부-비례-축소--v04337) |
| A431 색상 시험 판정 취소·격리 역사본/대표 비교의 정확한 조건 | [세 차례 관측과 한계](https://github.com/zpstudios/kotu/blob/67c4b1c834a15e45b53f8d5f8e29466dc5ce9172/docs/DEVICE-CHECKS.md#L59-L61) |
| A434 당시 필터 검정 잔류·기존 결함 판정·슬라이더 관측 한계 | [당시 판정](https://github.com/zpstudios/kotu/blob/67c4b1c834a15e45b53f8d5f8e29466dc5ce9172/docs/DEVICE-CHECKS.md#L55). 필터 후속 수리는 [A435](requirements-completed-index.md#a435)이며 당시 관측을 바꾸지 않음 |
| 이전 문서 다이어트의 원문과 회수 경로 | [요구 색인](requirements-completed-index.md) · [실기기 색인](device-checks-index.md) · [인수인계 색인](handover-index-through-2026-11-17.md) |

<a id="protected-material"></a>
## 보호 자료 위치

최신 설치 백업은 [HANDOVER](../HANDOVER.md)의 현재 상태로 찾는다. 아래는 이전 보호 대상을 찾는 경로다. 로컬 자료의 존재 여부와 Git 원문에 경로가 남아 있다는 사실은 다르다. 삭제·수정·재생성으로 부재 사실을 덮지 않는다.

| 자료 | 회수 위치와 보호 범위 |
|---|---|
| v0.436.0 설치·설정 백업과 v0.437.0 교체 기준 | `artifacts/a439-followup/pre-update-backup/` 1,191파일과 상위 `backup-coverage.json`·`pre-update-request*.json`·`pre-update-startup-log.json`, `pre-install-v0.437.0-ready*.json`. 백업을 재생성하지 않고 설치 직전 재검증했으며 원본과 설치 직전 기준을 모두 수정·삭제하지 않음 |
| v0.435.0 설치·설정 백업과 A439 첫 공식 배포 근거 | `artifacts/a439-review/pre-update-backup/` 1,191파일, 상위 `backup-coverage.json`·`pre-update-request*.json`·`pre-update-final-settings.json`·`pre-update-revalidation.json`과 `official-release/`. 후속 v0.436.0 백업과 별개로 수정·삭제하지 않음 |
| v0.434.0 설치·설정 백업과 A438 공식 배포 근거 | `artifacts/a438-review/pre-update-backup/` 1,371파일과 상위 백업·재검증·설정 기준 JSON, `official-release/` — [보호 당시 상태](https://github.com/zpstudios/kotu/blob/ab6e992c043ddc8f015b7b1fe1e85414c81e03cd/docs/HANDOVER.md#L8). 새 A439 백업과 별개로 수정·삭제하지 않음 |
| v0.433.9 설치·설정 백업과 공식 배포 근거 | `artifacts/a437-review/pre-update-backup/` 1,182파일, 상위 백업·재검증·설정 기준 JSON과 `official-release/`의 공식 Full 및 검증 근거 — [당시 위치와 검증](https://github.com/zpstudios/kotu/blob/6403859a1df482d3cc4a4852166d959fd1414bee/docs/HANDOVER.md#L5-L8). A438의 새 백업과 별개로 수정·삭제하지 않음 |
| v0.433.8 설치·설정 백업 | `artifacts/a435-review/pre-update-backup/`와 상위 기준 JSON — [백업 범위와 설치 결과](https://github.com/zpstudios/kotu/blob/5288d2cfc0c782154773444befd37c39573dbf42/docs/HANDOVER.md#L5-L8). A437의 새 백업과 별개로 수정·삭제하지 않음 |
| 이전 설치·설정·기준 JSON | [전체 보호 경로 및 설치 결과](https://github.com/zpstudios/kotu/blob/67c4b1c834a15e45b53f8d5f8e29466dc5ce9172/docs/HANDOVER.md#L36). A434 v0.433.7, A433 v0.433.5, A432 v0.433.4, A431/A430 및 이전 v0.433.3·v0.433.2·v0.433.0·v0.432.2 백업과 독립 복구 보존본 포함 |
| 백업별 상세 근거·철회된 A433 배포 | [당시 배포 기록](https://github.com/zpstudios/kotu/blob/67c4b1c834a15e45b53f8d5f8e29466dc5ce9172/docs/HANDOVER.md#L7-L26). A433 정정판은 기존 v0.433.5 보호 백업을 재사용했으며 철회판을 요구 완료로 세지 않음 |
| 과거 v0.427.0 백업 경로 조회 부재 | [A427 당시 기록](https://github.com/zpstudios/kotu/blob/67c4b1c834a15e45b53f8d5f8e29466dc5ce9172/docs/HANDOVER.md#L23) · [최종 확인](https://github.com/zpstudios/kotu/blob/67c4b1c834a15e45b53f8d5f8e29466dc5ce9172/docs/HANDOVER.md#L36). 현재 존재를 주장하지 않음 |
| 복구 문서 4개와 독립 복구 보존본 | [이관·중복 폐기 판단](recovered-notes-review-A417.md). 독립 보존본은 보호를 유지 |

고정 링크의 파일과 행 범위는 각 링크에 적힌 커밋의 스냅샷 기준이다. 로컬에서도 git show 커밋:경로로 회수할 수 있다. 이 색인은 과거 결과를 현재 승인이나 전 환경 통과로 승격하지 않는다.
