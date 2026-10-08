# A395 — Fit 통합 분할 버튼 인수인계

작성/검증: 2026-10-08. 구현 기준: master, `582ad99` 이후 작업, 제품 v0.391.0.
상태: **구현·로컬 빌드/자동시험·직접 UI 검증·정식 배포 완료.**

## 확정된 요청

사용자는 영상 하단에서 오른쪽 세 번째 Fit 아이콘과 두 번째 ▼가 관련 기능임을 확인했다.
본체는 현재 선택한 맞춤 방식을 재적용하고, ▼는 옵션 메뉴를 연다는 설명 뒤에
`[현재 맞춤 방식 | ▼]`처럼 하나로 연결된 분할 버튼을 제안했고 다음과 같이 승인했다.

> 그렇게 변경. 다만 두 버튼이 차지하고 있던 영역 전체를 통합된 긴 하나의 버튼이 동일하게 차지하도록 유의할 것. (컨텍스트 위험하니 즉시 인수인계)

- **전체 외곽 크기70×32 DIP**: 현재 본체32 + 내부 간격6 + 화살표32의 영역 전체를 보존한다.
  간격만 없애64 DIP로 줄이거나 과거 SplitButton 폭84 DIP로 넓히면 안 된다.
- 두 조작부가 한 개의 긴 버튼으로 보이도록 외곽을 연결하고 내부 분할을 표시한다.
  본체와 ▼의 별도 동작은 유지한다. 내부 너비 배분은 구현에서 정하되 전체70 DIP가 기준이다.
- 기존 외곽 좌우 끝·수직 정렬·바 높이·외부 간격을 유지하여 옆 CC와 전체 화면 버튼이 이동하지 않는다.
  전체 화면 버튼은 통합 대상이 아니다. 시스템 배율에 따른 실제 픽셀 크기와 DIP를 구분한다.
- 직접 대상은 첨부 화면의 영상 Fit이다. 이미지·문서도 같은 스타일을 사용하므로 공유 스타일을
  수정한다면 영향 범위를 검토한다. 관련 없는 모듈의 배치/동작 변경으로 확대하지 않는다.
- 이 범위에서는 종전 A365의 독립 버튼 요구보다 이번 사용자 승인이 우선한다. 재승인 질문은 필요 없다.

## 보존할 동작

- 본체: 마지막 선택 옵션의 아이콘/툴팁 표시, 클릭 시 같은 옵션 재적용.
- ▼: Original / Contain / Fit width / Fit height 메뉴, 선택 즉시 적용 및 본체 표시 갱신.
- 영상 변경 시 Contain으로 초기화. 파일이 없는 상태에서도 표시하되 비활성.
- 기존 F/A 단축키와 비활성 차단, 키보드 탐색·포커스·툴팁·접근성 및 hover/pressed/disabled 표시.
- 재생·취소·해제·저장·워커의 기존 처리 순서. A386의 잔여 발견 R1~R12는 이번 구현 범위가 아니다.

## 코드 진입점과 함정

| 위치 | 확인/수정할 부분 |
| --- | --- |
| `src/KOTU.Module.Video/VideoPlayerView.xaml` | `Grid.Column="11"`의 `StackPanel Spacing="6"`, `FitButton`, `FitOptionsButton`, 메뉴 및 A365 주석 |
| `src/KOTU.Module.Video/VideoPlayerView.xaml.cs` | `UpdateFitButton`, `UpdateFitEnabled`, `OnFitClicked`→`ApplyLastFitOption`, 각 `OnFit…Clicked`→`SelectFitOption` |
| `src/KOTU.App/App.xaml` | `BottomBarButtonStyle`와 `BottomBarFitOptionsStyle`의32×32/모서리/테두리; 일반 버튼 영향 주의 |
| `src/KOTU.Module.Image/ImageViewerView.xaml` | 같은 Fit 스타일과32+6+32 구조의 영향 확인 |
| `src/KOTU.Module.Document/DocumentView.xaml` | `FitControls`의 같은 구조; 외부 `Margin="6,0,0,0"`를 내부 간격과 혼동하지 않기 |

최대 함정은 **연결하면서 기존6 DIP까지 제거해 전체 폭이 줄어드는 것**이다.
기존 두 컨트롤을 고정70 DIP 영역 안에서 연결하는 방식도 가능하며 WinUI `SplitButton` 사용 자체가
요구사항은 아니다. `docs/REQUIREMENTS-ARCHIVE.md` A144 이력에서는 SplitButton84×32를 두 버튼으로
바꿨고 내부 분할 비율 제어 문제를 기록했다. App.xaml에는 WinUI 기본 DropDownButton 스타일 키를
`BasedOn`으로 참조해 런타임 XAML 파싱이 실패했던 이력도 있다. 검증되지 않은 기본 스타일 키를
추가하지 말고, 기존 컨트롤·핸들러를 활용해 변경 범위를 줄인다. 변경 후 낡은 A365 주석도 맞춘다.

## 인수인계 당시 완료 조건

1. 위 요구대로 구현하고 전체70×32 DIP 및 주변 배치 불변을 정적으로 대조한다.
2. 로컬 Release/x64 빌드와 변경에 맞는 기존 검사를 실행한다. 단순 스타일을 복제하는 새 시험은
   불필요하지만 WinUI 런타임 리소스/템플릿 오류와 실제 배치는 컴파일만으로 검증됐다고 하지 않는다.
3. **컴퓨터 사용하기 스킬로 직접 새 빌드를 실행**하여 빈 상태와 영상 재생 상태를 확인한다.
   변경 전후 외곽/주변 버튼 위치를 비교하고, 본체 재적용·네 옵션 선택/아이콘 갱신·키보드·비활성을
   확인한다. 가능한 배율/테마도 확인하되 실제 수행하지 않은 조합은 미검증으로 명시한다.
4. 결과와 남은 한계를 문서에 반영한다. 현재 버전이 그대로라면 다음 제품 버전은 v0.391.0이다.
   `master`에서 버전별 커밋 후 별도 승인 질문 없이 즉시 push하고 원격 빌드/릴리스를 확인한다.

로컬 .NET SDK는 `artifacts/dotnet/dotnet.exe`에 있다. `DOTNET_CLI_HOME=artifacts/dotnet-home`,
`NUGET_PACKAGES=artifacts/nuget`를 사용한다. 과거 문구인 "dotnet 없음"을 현재 사실로 취급하지 않는다.
이전 UI 시험 바이너리는 `artifacts/a386-4-publish/KOTU.exe`지만 새 변경 검증에는 새 publish를 사용한다.
컴퓨터 사용하기 스킬과 도구는 다음 세션의 현재 목록에서 다시 읽고, 실행 대상 버전을 확인한다.
시험용 파일/설정은 복원하며 사용자에게 직접 검증을 떠넘기지 않는다.

## 최초 인수인계의 검증 범위

현행 XAML의32+6+32와 공통 스타일의 높이32, 이벤트 처리의 역할을 코드로 확인했다.
이번 변경은 문서뿐이며 **통합 버튼 구현·빌드·실제 화면 검증을 아직 수행하지 않았다.**
A386의 앞선 실제 UI 검증은 `docs/A386-UI-WORKER-AUDIT.md` §9에 있으며 A395 검증을 대신하지 않는다.

## 후속 구현(2026-10-08)

- `VideoPlayerView.xaml`의 영상 Fit만 변경했다. 고정70×32 DIP 안에서 본체38 + 화살표32를 간격0으로
  연결하고 바깥 모서리만 둥글게 했다. 중앙 테두리는 화살표의 왼쪽 한 줄이다.
- 본체가 기존 내부 간격6 DIP를 차지하므로 화살표 시작 위치와 전체 외곽 끝은 그대로다.
  자막/전체 화면 위치·외부 간격6·기존 이벤트/이름/비활성 조건을 유지한다.
- 공통 스타일·이미지·문서·C# 처리 코드는 변경하지 않았다. 제품 버전은 v0.391.0이다.
- Release/x64 전체 빌드0경고·0오류, 자동시험566/566, win-x64 self-contained publish,
  구조20프로젝트·릴리스 게이트7변형 검사 통과.

## 직접 UI 검증 결과

- 컴퓨터 사용하기 스킬로 기존 실행본을 정상 종료하고 비교용 v0.390.0과 새 로컬 publish를 각각 실행했다.
  새 대상은 `artifacts/a395-publish/KOTU.exe`, 설정 화면의 Current version v0.391.0을 확인했다.
  검증은 Windows 배율150%, 앱 배율150%/100%, 다크 테마에서 수행했다(약21:37~21:45 KST).
- 내장 `test-clip.mp4`를 사용했다. 실제 사용자 영상·문서는 편집하지 않았다.
  종료 후 시험 전 설정 백업을 복원하고 SHA-256 일치를 확인했다.

| 항목 | 관찰 결과 |
| --- | --- |
| 폭/연결 모양 | 150%에서 변경 전 분리된 두 버튼과 비교해 외곽·자막/전체 화면 위치가 유지되고 내부 틈만 채워짐. XAML의70×32 및38+32를 별도 대조 |
| 빈 상태 | 150%/100% 모두 버튼이 계속 보이며 Fit 본체/옵션 둘 다 비활성. 빈 상태 F/A는 영상을 열거나 맞춤을 활성화하지 않음 |
| 옵션 선택 | Original·Contain·Fit width·Fit height 각각 메뉴 선택 뒤 본체 아이콘 갱신, 메뉴 닫힘, 일시정지 위치0:05 유지 |
| 본체/단축키 | Original 상태 본체 클릭은 메뉴를 열지 않고 같은 옵션 유지. A로1:1 전환, F로 현재 옵션 재적용 |
| 키보드/취소 | Tab 후 옵션 쪽 포커스 윤곽 확인. 열린 메뉴에서 위 방향키로 Fit height 이동, Enter로 선택. Escape는 메뉴만 닫음 |
| 화면 모드 | Enter의 셸 화면 모드 전환 후 Escape 복귀에서도 연결 모양과 기존 선택 유지 |
| 배율/재진입 | 설정에서100%로 변경 후 빈 상태·활성 상태·메뉴 표시 정상. 영상 모듈 재진입/내장 영상 재로드 후 Contain 아이콘으로 초기화 |

검증 한계: 화면 비교는 육안 비교이며 실제 픽셀 경계 자동 계측은 하지 않았다. 정확한70×32 DIP는
XAML 치수/스타일 대조로 확인했다. 라이트·고대비 테마, 다른 배율, 모니터 이동, 내레이터,
터치 입력은 미검증이다. 키보드만으로 메뉴를 여는 동작은 완료 판정하지 않는다: Tab 윤곽 뒤
Alt+Down으로 메뉴 열림을 관찰하지 못했고 Enter는 셸 화면 모드로 전달됐다. 열린 메뉴의 방향키/Enter
선택은 위 표처럼 확인했다. 이 키 전달 현상의 구버전 동일 재현은 별도로 하지 않았으며,
이번 변경에는 키 처리/C# 수정이 없다. 기존 취소·해제·저장 경로는 코드 무변경 및 자동 회귀로 보존을
확인했고 A386의 장애·장치 시험을 이번에 다시 수행하지 않았다.

증거: `artifacts/a395-build.log`, `artifacts/a395-tests.log`, `TestResults/a395`,
`artifacts/a395-publish.log`, 이번 대화의 실제 화면/접근성 관찰. 로컬 publish의7z.dll은 기존
`artifacts/native/7zip/7z.dll`에서 배포 준비와 같은 위치로 복사했다. Setup/Standalone 자체의
설치·UI 시험은 이 로컬 실행본 시험과 구분한다.

검증한 Video DLL SHA-256: `26410906FD522B04317C01EEA7D59C82474809F57D20F56EE5660CC8C6974483`.
검증한 resources.pri SHA-256: `B3C16C3D59C86C14A6D770C7A28E787D15FFDAB636C1E85E283F657A78FE178A`.

## 배포 기록

- 구현 커밋 `57cf5a4a4eda3ee78fe19bda9451c9e2868c0f70`을 master에 즉시 push했다.
- [build 37779489869](https://github.com/zpstudios/kotu/actions/runs/37779489869) 성공.
  원격 전체 빌드·자동시험·Explorer COM 전달 검사 통과.
- [release 37779489888](https://github.com/zpstudios/kotu/actions/runs/37779489888) 성공.
  원격 전체 빌드/시험·publish·시작·설치본 정합성·Standalone 검사 및 업로드 통과.
- [v0.391.0 정식 릴리스](https://github.com/zpstudios/kotu/releases/tag/v0.391.0)는
  2026-10-08 21:55:41 KST에 게시됐다. draft/prerelease가 아니며 배포물8개 모두 uploaded다:
  Setup.exe, Portable.zip, Standalone.exe, full/delta nupkg, assets.win.json, releases.win.json, RELEASES.
- 원격 `v0.391.0` 태그가 위 구현 커밋을 정확히 가리키는 것을 확인했다.
  원격 자동 설치 검사는 통과했지만 게시된 설치본을 이 PC에서 다시 설치해 수동 UI 시험한 것은 아니다.
