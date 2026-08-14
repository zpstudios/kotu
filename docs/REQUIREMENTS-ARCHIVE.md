# KOTU 요구사항 아카이브 — 완료 항목 상세

> `docs/REQUIREMENTS.md` 본문 다이어트(2026-08-13, 사용자 지시)로 옮겨 온 **완료(결번) 항목의 확정·구현 상세**다.
> 옮긴 기준: 미반영 항목·최근 결정이 **더는 참조하지 않는** 완료 상세. 본문에는 한 줄 요약만 남겼다.
> A번호는 영구 ID — 여기 있는 기록도 지우지 않는다. 완료 시점 사실이므로 이후 항목이 대체했을 수 있다(본문이 우선).

## 0. 브랜딩 · 명칭 (전역)

- ※ A46(**앱 이름 ZP → KOTU (King Of The Util)**, v0.86.0) 완료 — 결번. 확정·구현 내역:
  - **범위 = 표시명 + 시스템 등록 ID**(사용자 확정 2026-08-10). exe·어셈블리·솔루션·네임스페이스(`WinUtil.*`)는
    **초기 코드명 그대로 유지** — CI·설치 경로·업데이트 채널을 깨지 않기 위함.
  - 표시 문자열은 `Branding.AppName` 하나를 참조하도록 정리(창 제목·웰컴·설정 안내문·버전 표기·트레이 툴팁/메뉴·
    시작 실패 대화상자). 다음 리네이밍은 이 상수 + `ExplorerIntegration.Brand`만 바꾸면 된다.
  - `BrandName`: KOTU-image/-video/-audio/-doc/-zip/-info (※ `-zip`→`-archive`는 A52에서).
  - 시스템 ID 이전: ProgID `KOTU.*`(+확장자별 `KOTU.archive.zip`), Capabilities `Software\KOTU`,
    RegisteredApplications 값 `KOTU`, 셸 verb `KOTU.ExtractHere`·`KOTU.Compress`, AppInstance 키 `KOTU-Main`,
    `%TEMP%\KOTU`, 설정 `%AppData%\KOTU\settings.json`, 파일 아이콘 `kotu-*.ico`, Velopack packId `KOTU`.
  - **이관 없음**(사용자 확정): 구 `%AppData%\ZP` 설정을 복사하지 않는다(구 폴더는 그대로 남겨 되돌릴 수 있게).
    대신 구 브랜드(ZP·WinUtil) 레지스트리 등록은 **첫 실행 1회 자동 청소**
    (`ExplorerIntegration.CleanUpLegacyBrandRegistrations`, 플래그 `integration.legacyBrandCleanupDone`).
    `LegacyBrands` 배열에 구 이름을 쌓는 구조라 다음 리네이밍도 같은 방식으로 처리된다.
  - **UserChoice(A38) 재지정**: ProgID가 바뀌어 기존 기본 앱 지정은 무효 → 설정 화면 "n/m"이 0으로 떨어지고,
    연결 토글을 켜면 A38이 새 ProgID로 자동 재지정(실패 시 기존대로 A25 딥링크 폴백).
  - 앱 아이콘: 4글자 "KOTU"는 16px에서 뭉개져 중립 아이콘을 **"KO"/"TU" 2줄**로 확정(사용자 선택).
    모듈 아이콘 우하단 연결 표식은 `zp` → `kotu`(폰트 44→30).
  - 자동 업데이트 단절: packId 변경으로 구 ZP·WinUtil 설치본은 업데이트가 끊긴다 → 릴리스 본문에 수동 재설치 안내.

- ※ A64(**리브랜딩 2단계 — exe·어셈블리·프로젝트·네임스페이스 전면 rename**, v0.88.0) 완료 — 결번.
  A46의 "표시명 + 시스템 ID만" 결정을 대체. 확정·구현 내역:
  - **계기**: UAC 대화상자에 `WinUtil.App.exe`가 노출됐다. 핵심은 *"WinUtil이라는 단어가 나오는 것"* —
    이제 **`KOTU.exe`** 로 표시된다. (같은 대화상자의 "게시자: 알 수 없음"은 코드 서명 문제로 **요구사항 아님**.)
  - `AssemblyName` = **KOTU** → 산출물이 `KOTU.exe`. 프로젝트는 `KOTU.App`이지만 exe 이름은 `KOTU`다.
  - 디렉터리·프로젝트 파일·솔루션·네임스페이스 전부 `WinUtil.*` → `KOTU.*` (테스트 포함, `git mv`로 이력 보존).
  - **레거시 식별자는 의도적으로 보존**: `LegacyBrands = ["ZP", "WinUtil"]`(구 등록 청소용),
    `%AppData%\WinUtil`·`WinUtil-Main`을 가리키는 주석. 이들은 과거를 가리키는 값이라 바꾸면 안 된다.
  - 함께 고친 것: `release.yml`의 `--mainExe`(→ KOTU.exe)와 `$(TargetName).pri` 폴백(→ `KOTU.pri`),
    publish 경로, `build.yml`, README·ARCHITECTURE·BUILD, 저장소 URL(`zpstudios/zpro` → `zpstudios/kotu`).
  - **덤으로 발견해 고친 것**: 자막 임시 폴더가 아직 `%TEMP%\WinUtil\subtitles`였다
    (A46은 "ZP" 문자열만 훑어서 놓쳤다) → `%TEMP%\KOTU\subtitles`.
  - ※ **실기기 확인 필요**: 기존 설치본을 자동 업데이트했을 때 ① 시작 메뉴·작업표시줄 바로가기가
    새 `KOTU.exe`를 가리키는지 ② 설치 폴더에 구 `WinUtil.App.exe`가 남는지
    ③ 파일 연결(ProgID의 `shell\open\command`)이 새 exe 경로로 갱신되는지 —
    ③은 v0.88.0에서는 **연결 토글을 껐다 켜야 갱신**됐다(등록 시점의 `ExePath`를 굽기 때문).
    → **v0.89.0의 A78이 이 수동 재등록을 없앰**(매 실행 시 경로 어긋남 감지 → 자동 재등록).
    실기기에서 ③이 실제로 확인됨(2026-08-10, .png 더블클릭이 '어떤 앱으로 열까요' 팝업으로 빠짐).

- ※ A52(**모듈 표시명 정리**, v0.87.0) 완료 — 결번. 확정 내역:
  `DisplayName` = **Image / Video / Audio / Document / Archive** (H/W Info는 현행 유지).
  v0.38.0 복수형·"ZIP" 지정과 v0.75.0 "Music" 확정을 대체. `BrandName`도 **KOTU-archive**로 일치.
  ※ 하드코딩돼 있던 우클릭 메뉴 라벨("Extract here with …"·"Compress with …")은
  `RegisterExtractHereMenu/RegisterCompressMenu(…, brandLabel)` 인자로 바꿔 **모듈 BrandName을 따르게** 했다 —
  다음에 모듈명이 바뀌어도 문구가 자동으로 따라온다.
  ※ **알려진 한계**: 셸에 이미 등록된 verb 라벨·ProgID 설명은 **재등록될 때만** 갱신된다.
  v0.86.0에서 우클릭 메뉴를 켜 둔 사용자는 설정에서 토글을 껐다 켜야 "KOTU-archive" 문구로 바뀐다.

- ※ A79(**브랜드 에셋 단계형 적용**, v0.119.0) 완료 — 결번. 확정·구현 내역:
  - **키 = `branding.assetLevel`** (settings.json, int, **기본 0**, 유효 0~3 — 범위 밖은 클램프).
    설정 화면에는 **노출하지 않는다**(사용자 확정). A36(v0.109.0)의 "Open settings.json"이 실험 경로다.
  - **레벨 구획**(사용자 확정): **0** 현행 무적용 / **1** ①② 아이콘 포인트 /
    **2** +③⑤ 워드마크·스피너 / **3** +④⑥ 마스코트·랜딩.
  - **단일 매핑 = `src/KOTU.App/BrandAssets.cs`** — `BrandPoint` enum + "지점 → 최소 레벨" 표 +
    `IsEnabled(BrandPoint)` 하나. **적용 지점 코드는 레벨 숫자를 절대 비교하지 않는다**(그게 이 항목의 본체).
    생성 스크립트 쪽 같은 표는 `packaging/brand.py` — 두 표는 같은 값이어야 한다.
  - 지점별 구현: **①②는 벡터**(GDI+ `BrandIcons`/`BrandPaw`, Pillow는 `brand.draw_paw` — 16px 가독성 때문에
    래스터 축소를 쓸 수 없다) / **③④⑥은 시트에서 잘라낸 래스터 조각**(`Assets/Brand/*.png`,
    `packaging/gen_brand_assets.py`가 생성) / **⑤는 조각 1장을 회전**시킨다.
  - ⑤ 적용 위치 = **설정 화면 파일 연결 토글의 진행 표시 한 곳**(A77). 나머지 링은 현행 유지("살짝씩").
  - **레벨은 시작 후 1회 읽고 캐시**한다. settings.json을 고치면 **앱을 다시 켜야** 반영된다(실시간 반영은 요구가 아님).
  - **⑥의 한계**: `site/`는 정적 페이지라 레벨 값이 자동으로 닿지 않는다. 레벨 3 로고
    `site/assets/logo-mark-brand.png`를 넣어 두고 **HTML의 `img src` 한 줄**(nav·푸터 2곳)로 바꾼다. 기본은 현행 유지.
    같은 이유로 **탐색기 파일 아이콘**(`Assets/fileicons/*.ico`)도 런타임 레벨이 닿지 않는다 —
    `gen_file_icons.py`를 레벨 인자로 다시 돌려 커밋해야 바뀐다(A52의 우클릭 라벨 한계와 같은 성격).
    **커밋되는 산출물(.ico·splash.png)은 언제나 레벨 0으로 생성된 것**이어야 한다.
  - 에셋 파일이 없거나 깨졌으면 **조용히 레벨 0의 모습**으로 떨어진다(SponsorAds 규칙).
    A3(아이콘 반전)·모듈 색 배경·A2/A68 인스턴스 테두리·배지 규칙은 그대로 유지된다.
  - **에셋 출처**(부록 B 41): 동료가 기르는 반려견 사진을 재료로 **AI 생성**(사용자 제작·제공) —
    라이선스 확인 요구 종결. 원본 시트는 `docs/assets/kotu-brand-sheet.png`에 보관하고
    **재배포물에는 조각만** 넣는다(시트 통째 동봉 금지).

- ※ A82(**개인 실명 노출 제거 — 저작·표기 주체를 `ZP Studios` 한 이름으로 통일**, v0.90.0) 완료 — 결번.
  원칙: **개인 실명·개인 메일은 배포물·저장소·문서 어디에도 표시하지 않는다.**
  이 문서에도 다시 적지 않는다(적는 순간 공개 저장소에 남는다 — v0.90.0에서 실제로 그래서 지웠다).
  확정·구현 내역:
  - `LICENSE` → **`Copyright (c) 2026 ZP Studios`**. GitHub 저장소 상단 "MIT license" 탭에 노출되던 곳이 여기다.
  - **표기 통일**(사용자 확정): `release.yml`의 `--packAuthors`가 `KOTU Studios`로 달랐다 → **`ZP Studios`**.
    저작 주체 = **ZP Studios**(회사), 제품명 = **KOTU**. 두 층을 섞지 않는다.
  - `Directory.Build.props`에 `<Company>`·`<Authors>`·`<Copyright>` 추가 → 빌드된 exe의 [속성 > 자세히]에도 표기된다.
    **다음에 표기를 바꿀 땐 이 4곳(props·LICENSE·release.yml·site 푸터)을 함께 본다.**
  - `site/index.html` 푸터에 `© 2026 ZP Studios` 추가.
  - **git 커밋 작성자**(사용자 확정): **과거 이력은 재작성하지 않는다** — force push로 해시가 전부 바뀌면
    기존 클론·릴리스 태그와 어긋난다. 저장소 로컬 config만 교체 → 이름 `ZP Studios` / 메일 `zpstudiosdev@gmail.com`.
    ※ **한계**: v0.89.0 이전 커밋의 작성자 표기는 GitHub에 그대로 남는다. 완전 제거는 이력 재작성뿐이고, 사용자가 거부했다.
  - 로컬 스크래치 폴더 `_to_delete/`(추적 안 되던 git 잠금 파일 잔해 + 실명 포함 번들) 삭제.
  - ※ 이 리포에서 커밋할 땐 **`-c user.name=...` 같은 일회성 오버라이드를 쓰지 말 것** — 로컬 config가 정답이다.

## 1. 셸 · 시작 메뉴

- ※ A53(**"New window" → "New Instance"** 표기 통일, v0.87.0) 완료 — 결번.
  시작 메뉴 "New Instance", 탐색기 우클릭 "Open in new instance",
  설정 토글 "Always open files in a new instance" + 설명문. 단축키 Ctrl+N·동작은 그대로.

- ※ A56(**제목표시줄 정리 + 인스턴스 번호 접두**, v0.87.0) 완료 — 결번. 확정 내역:
  제목 형식 = **`● [2] KOTU Document — sample.pdf`**
  (● = 미저장 표시(A37) → `[n]` = 인스턴스 번호 → 내용. 잘려도 앞 두 표식이 남게 이 순서).
  **색상 배지(A2)는 그대로 유지**하고 제목 문자열에도 번호를 추가(사용자 확정) — 작업표시줄·Alt+Tab에서도 구분된다.
  창이 하나뿐이면 배지·번호 모두 숨김(기존 규칙 유지).
  ※ 배지 색은 9개뿐이라 10번째 창부터는 배지만 숨고 **제목 번호는 계속 표시**된다
  (`WindowManager.UpdateInstanceNumbers`가 실제 번호를 그대로 넘기고, 표시 여부는 창이 판단).

- ※ A96(**시작 메뉴 개편 — 번호·구분선·치수·밀착**, v0.116.0) 완료 — 결번. 확정·구현 내역:
  - **번호 배열 = All Readable이 1번, 나머지는 기존 순서 그대로 한 칸씩 밀기**(2026-08-13 사용자 확정).
    A59의 "기존 1~6 근육기억 유지, 신설은 7"을 **대체**한다.
    → `1 All Readable · 2 Image · 3 Video · 4 Audio · 5 Document · 6 Archive · 7 H/W`, Settings = 0 유지.
    갱신 지점: `MainWindow.ModuleShortcuts` · `BuildStartMenu` 항목 순서(툴팁 힌트는 배열에서 파생) ·
    **A34 키 맵 표(아래 10.1장)** · `docs/A86-keymap.md`. 창 아이콘 매핑은 모듈 id 기준이라 번호와 무관.
  - **구분선 2개 추가**: ① 1번(All Readable)과 2번 사이 ② 하드웨어 인포와 Settings 사이.
    메뉴는 위→아래로 채우고 번호는 아래→위라 **1번이 최하단** — All Readable이 메뉴 맨 아래로 내려갔다.
  - **메뉴 폭 124 → 136**(+10%), **항목 높이 44 → 40**(−9.1%)·항목 상하 Padding 12 → 10.
    광고 카드가 메뉴 폭에 묶여 있어(`BuildSponsorCard`) 이미지 규격 **120×60 → 132×66**(= 136 − 카드 패딩 4),
    카드 MinHeight 60 → 66도 함께. 구분선 여백 3(A50)은 유지.
  - **플라이아웃 밀착**: WinUI `Flyout`은 대상과의 간격 오프셋을 속성으로도 테마 리소스로도 내놓지 않는다 →
    `FlyoutPresenter` 스타일에 **`Margin 0,0,0,-6`** 을 더해 끌어내렸다(배치가 프레젠터 DesiredSize 기준).
    **미세 조정은 이 숫자 하나** — 실기기 눈 확인 항목.

- ※ A50(**시작 메뉴 항목 선택성 개선 — 좌측 히트 영역 확대 + 항목 간격 축소**, v0.92.0) 완료 — 결번. 구현 내역:
  ① 플라이아웃 프레젠터 패딩을 0으로(좌우 16 데드존 제거) — Stretch인 항목 버튼이 메뉴 좌우
     가장자리까지 닿아, 포인터가 라벨보다 왼쪽에 있어도 항목이 선택된다.
  ② 그룹 구분선 상하 여백 8→3, 프레젠터 상하 패딩은 StackPanel Padding 상하 4로 대체.
     실기기에서 오선택률 확인 필요.
  ※ **A96(v0.116.0)이 치수를 개정**: 항목 Padding `10,12,10,12` → `10,10,10,10`,
     A31의 최소 높이 **44 → 40**, 메뉴 폭 **124 → 136**. 구분선 여백 3은 그대로.

- ※ A68(**인스턴스 색상 코딩 확장** — 창·트레이 아이콘까지, v0.103.0) 완료 — 결번. 확정·구현 내역:
  - 팔레트 = A2 배지의 9색 그대로, **10번째 인스턴스부터 1번 색부터 순환**(사용자 확정 — 부록 B 32번).
    팔레트는 `InstanceIcon.ColorFor`로 이동 — 타이틀바 배지·아이콘 테두리·트레이가 공유.
  - 창 2개 이상이면 창 아이콘(타이틀바·작업표시줄)과 창별 트레이 아이콘에
    **인스턴스 색 테두리 링 + 우하단 원형 번호 배지**(타이틀바 배지와 같은 ①②③ 형태)를 합성.
    층 분리 유지: 모듈 색 = 아이콘 본체(A3), 인스턴스 색 = 테두리. 창 1개면 무테두리 원본(숨김 규칙 일관).
    ※ **A54(v0.118.0)에서 트레이 아이콘의 원형 번호 배지는 제거**(인스턴스 색 테두리만 유지) —
    트레이가 값 2줄을 그리게 되어 배지가 글자를 덮기 때문. **창 아이콘 배지는 그대로 존치**하고,
    번호는 제목 `[n]`(A56)이 계속 알려 준다.
  - 합성 = System.Drawing/GDI+(A18 센서 트레이와 같은 도구)로 기존 모듈 .ico 위에 그린 뒤
    GetHicon → WM_SETICON(`WindowIcon`)·Shell_NotifyIcon(`TrayIcon`). (경로·번호·크기) 키의 프로세스 수명 캐시.
  - 번호 변경(중간 창 닫힘 등)·모듈 전환 시 `SetInstanceNumber`/`ApplyWindowIcon` →
    `RefreshShellIcons`로 재합성 — `WindowManager.UpdateInstanceNumbers` 경로에 연결.
  - 타이틀바 배지는 이미 원형(16×16, CornerRadius 8)이라 XAML 변경 없음.
    센서 트레이 아이콘(A18)은 값 표시가 우선이라 인스턴스 테두리 미적용(구현 시 결정).

- ※ A69(**최소화하면 트레이로 숨기기**, v0.104.0) 완료 — 결번. 확정·구현 내역:
  최소화 시 **작업표시줄·Alt+Tab 목록에서 제거**하고 트레이 아이콘으로만 복귀, **전 모듈 적용**(사용자 확정 2026-08-10).
  - **감지** = `AppWindow.Changed`에서 `OverlappedPresenter.State == Minimized` 전이 검사
    (`MainWindow.OnMinimizeStateChanged`) — A55 `TrackNormalBounds`가 실증한 이벤트 경로.
    WindowMinSize 서브클래스에 WM_SYSCOMMAND(SC_MINIMIZE)를 더하는 대안은 채택 안 함
    (애니메이션 전에 개입 + wParam 하위 4비트 마스킹 판정 부담). 이벤트 시점엔 좌표가 이미 -32000으로
    이동한 뒤라 "최소화 애니메이션 후 Hide" 순서가 자연 성립. 실제 숨김은 DispatcherQueue로 미뤄 재진입 회피.
  - **숨김** = `AppWindow.Hide()`(작업표시줄 버튼 제거) + 숨김 동안만 `WS_EX_TOOLWINDOW`
    (신규 P/Invoke 헬퍼 `AltTabExclusion` — Hide가 주 동작, 스타일은 셸 변형 방어의 보조선).
  - **복귀** = 트레이 좌클릭·메뉴 'Activate window'(기존 `ActivateRequested` → `BringToFront`)가
    스타일 원복 → `AppWindow.Show()` → 최소화면 `OverlappedPresenter.Restore()` → 활성화 순으로 되살린다.
    파일 열기 재사용(A24)·단일 인스턴스 재전달이 숨김 창을 고르는 경우도 같은 `BringToFront` 경로라 함께 복귀.
  - **앱 생존 표시** = 창별 트레이 아이콘이 숨김 중에도 그대로 남으므로 추가 표시 없음(구현 시 결정).
    A39 핀(always on top) 창도 최소화 버튼을 눌렀다면 예외 없이 숨긴다(일관성).
    ※ A61(v0.111.0) 구현 후 재확인: **접힌 창도 그대로 성립**한다 — 최소화 판정은 프레젠터 상태,
    복귀는 `Restore()`라 접힌 크기 그대로 되살아난다(A55 추적은 Restored 상태만 보므로 개입 없음).
  - **창 0개 = 종료 로직 정리**: 숨김은 닫힘이 아니다 — `WindowManager` 창 목록 제거는 `Closed`에서만
    일어나므로 마지막 창까지 숨겨도 프로세스 유지(코드 변경 없이 성립, 주석으로 명문화).
    진짜 닫기(X·트레이 Close·Exit KOTU)만 종료 경로. 숨김 창의 닫기에서 미저장 확인(A37)이 필요하면
    대화상자가 보이도록 먼저 복귀시킨다(`ConfirmThenCloseAsync`).
  - **A55 정합**: 숨김 상태로 닫혀도 저장값은 숨기기 전 일반 상태 값 — 최소화(-32000)는
    `TrackNormalBounds`가 이미 거르고, `SaveWindowBounds`의 비Restored 분기가
    `_lastNormalPos/Size`를 저장한다(기존 동작 그대로, 변경 없음).

- ※ A55(**창 크기 + 위치 저장/복원**, v0.95.0) 완료 — 결번. 확정 내역:
  종료 시점의 최종 위치(X/Y)를 `window.x`·`window.y`(물리 픽셀, 기존 width/height와 동일 기준)로 저장하고
  다음 실행 시 복원. 저장소는 현행 settings.json(`_settings.Get/Set`), 마지막으로 닫힌 창이 이긴다(창별 저장은 A70 별도).
  - **최대화**: 최대화로 닫으면 `window.maximized=true`로 저장하고 다음 실행 시 최대화로 복원.
    복원(Restore Down)용 일반 크기·위치는 최대화 직전 값 유지 — `AppWindow.Changed`에서
    Restored 상태의 크기·위치만 추적(`TrackNormalBounds`)해 두었다가 그 값을 저장.
  - **전체화면**: 일시 모드로 취급, 저장하지 않음 — 전체화면(최소화도 동일)으로 닫으면 직전 일반 크기·위치만 저장.
  - **화면 밖 보정**: `DisplayArea.GetFromRect(Nearest)`의 WorkArea와 겹침 검사 —
    가로 노출 48px 이상 + 타이틀바 세로 밴드가 WorkArea 안이면 통과, 아니면 가장 가까운 WorkArea 안으로 클램프.
  - **다중 인스턴스(A24) 오프셋**: 두 번째 이후 창은 저장 위치 + 32px × (인스턴스−1) 계단식으로 열고,
    오프셋 결과도 화면 밖 보정을 통과시킨다.
    ※ **A89(v0.114.0)가 이 오프셋을 대체 — 오프셋 없이 그대로 승계**(아래 A89 항목).
  - **A40 정합**: 최소 크기 클램프(`WindowMinSize.MinPhysical`)를 먼저 반영한 최종 크기로 보정·이동(`MoveAndResize` 1회).

- ※ A89(**새 창 크기·위치 = 마지막에 닫은 창을 그대로 승계**, v0.114.0) 완료 — 결번. 확정 내역:
  `MainWindow.RestoreWindowBounds`에서 A55의 계단식 오프셋(`32 * _manager.OpenWindowCount`)을 제거해
  저장값(`window.x/y/width/height`)을 **오프셋 없이 그대로** 적용한다. 화면 밖 보정(`ClampToWorkArea`)과
  A40 최소 크기 클램프는 그대로 유지. 저장 측(`TrackNormalBounds`·`SaveWindowBounds`)은 이미
  "마지막으로 닫힌 창이 이긴다"라 무수정.
  - 구현 시 결정(사용자 확정): 승계 결과가 **살아 있는 창과 정확히 겹쳐도 그대로 둔다** — 비켜 주는 로직 없음.
  - `WindowManager.OpenWindowCount`는 남겨 둔다(인스턴스 번호 계산 등 다른 용도 대비).

## 2. 내장 탐색기 · 오버레이

- ※ A58(**좌/우 오버레이 입력 매핑 전면 변경**, v0.100.0) 완료 — 결번.
  A32의 "Ctrl 홀드 + 2연타 고정" 확정 내역을 **대체**. 확정·구현 내역:
  - **키 할당 = Shift(우측 정보) / Alt(좌측 리스트)** — Ctrl은 오버레이에서 손 뗌 (부록 B 26번).
  - 사이드별 **4상태 머신**(MainWindow, `OverlayState`): 닫힘 → 키 홀드 = **반투명 덮기**(아크릴,
    메인 크기 불변) → **2초 이상 홀드 = 반투명 고정**(키를 떼도 유지 — 2초 넘겨 뗐을 때 = 고정 유지) /
    닫힘에서 **2연타(450ms) = 불투명 밀어내기**(OpaqueDocked — 배경 불투명 + 메인이 반대쪽 7*로 축소,
    양쪽 다면 3:4:3, 좌우 각 30% 유지. MainWindow CenterArea의 도크 컬럼이 실제 폭을 차지).
    **고정 해제 = 2연타**(반투명 고정·불투명 밀어내기 **둘 다** — 반투명 고정에서 2연타는 "해제",
    불투명으로 가려면 닫힌 상태에서 2연타).
  - **홀드 판정은 다른 키·포인터 클릭이 개입하면 취소**(OS Alt 메뉴 모드와 같은 규칙, 2연타 카운트도
    리셋) — Shift+클릭 다중 선택·Shift+더블클릭(새 인스턴스)·A84 Shift 조합의 공통 안전장치.
    단, **그 오버레이 자신 안에서의 클릭은 예외**(Alt 쥔 채 리스트에서 파일 더블클릭으로 여는 기존 흐름 보존).
  - 텍스트 입력 컨트롤 포커스 시 통과(A32 유지 — Shift 대문자 입력 방해 금지),
    Alt 단독 키의 OS 메뉴 모드 충돌 처리는 기존 방식 유지(오버레이 관련 down/up만 소비).
  - 안내 문구 상태 구분: 반투명 고정 = "Pinned — press X twice to unpin" /
    불투명 = "Docked — press X twice to close" (X = Shift/Alt).
  - 오버레이 컨트롤에 `OverlayMode { TranslucentOver, OpaqueDocked }` + `SetState` 도입,
    셸에 **`SetDockedState(listDocked, infoDocked)` 공개 API** — A81(진입 경로별 기본 상태, v0.101.0)이 사용.
  - 고정·불투명 상태는 콘텐츠 전환을 넘어 유지(기존 규칙), 콘텐츠 없는 화면(설정·H/W)에서는 숨김.
  ※ **A86(v0.121.0)이 키를 대체했다**: Alt/Shift → **Z/X**, Alt OS 메뉴 모드 처리 제거,
  **열림 상태에서 해당 키 1회 = 닫기** 신설(2연타 판정은 "첫 탭 이전 상태" 기준으로 조정).
  홀드/2초/2연타 전이·홀드 취소 안전장치·SetDockedState는 그대로 유효.

- ※ A81(**오버레이 기본 표시 상태 = 진입 경로별**, v0.101.0) 완료 — 결번. 확정·구현 내역:
  - **파일을 직접 열며 시작**(탐색기 더블클릭·연결 프로그램·드래그 등 파일 인자, "새 창으로 파일 열기" 포함)
    → **좌·우 오버레이 없음**이 기본(뷰어 영역 최대) — 기본 상태가 이미 닫힘이라 주입 없음.
  - **모듈만 실행**(시작 메뉴·앱 아이콘 등 파일 인자 없는 시작, Ctrl+N 빈 새 창 포함) →
    **불투명 밀어내기, 좌·우 둘 다**(부록 B 30번). 창 생성 진입 1회만 `WindowManager`가
    `SetDockedState(true, true)`를 주입 — 기본 화면(H/W)에서는 상태만 남았다가 파일 모듈로
    전환하는 순간 도크로 나타난다.
  - **유지/재적용 규칙**: 이후 모듈 전환·파일 열기에서는 재적용하지 않고 사용자가 바꾼 상태를
    그대로 유지(기존 "고정은 콘텐츠를 넘어 유지" 규칙 그대로 — 빈 모듈 간 전환도 도크 유지).
    세션·재시작 간 기억 안 함 — A55 창 상태 저장에 오버레이 상태는 미포함, 매 시작 진입 규칙만 적용.
  - **빈 모듈 상태(파일 없이 연 파일 모듈)도 오버레이 컨텍스트로 인정**(A58 hasFile 가드 완화):
    좌측 리스트 = 모듈 시작 폴더(마지막 폴더 v0.55.0/바탕화면 — 중앙 빈 상태 탐색기와 같은 규칙 공유),
    우측 정보 = "No file open" 플레이스홀더. 입력 판정(2연타 등)도 빈 모듈에서 성립 —
    기본 도크를 닫을 수 있어야 하므로(전이 규칙 자체는 A58 그대로). 설정·H/W·빈 셸은 종전대로 숨김.
  - ~~빈 모듈에서 좌측 리스트가 **불투명 도크면 중앙 탐색기는 숨김**(같은 폴더 목록이 나란히 두 번
    보이는 중복 제거) — 도크를 닫으면 복귀.~~ **A93(v0.120.0)이 대체**: 중앙이 리스트가 아니라
    썸네일 뷰가 되면서 중복이 사라져 **항상 표시**한다. 반투명(홀드·고정)이 덮기 표시인 것은 그대로.

- ※ A86(**오버레이 동작·단축키 재정의 — Z/X + Enter 일괄 토글 + 경계 버튼**, v0.121.0) 완료 — 결번.
  구현 사양 = `docs/A86-keymap.md`(2026-08-13 사용자 합의 확정, 부록 B 53). 확정·구현 내역:
  - **키 전환**: 좌 = Alt → **Z** / 우 = Shift → **X** (`MainWindow.SideForKey`). 홀드=반투명 덮기 /
    2초 홀드=반투명 고정 / 2연타=불투명 도크·해제(A58 전이)는 그대로, 닫힌 오버레이 꺼내기도 Z/X.
    **A86 신설 전이: 열림 상태(반투명 고정·불투명 도크)에서 해당 키 1회 = 그 쪽 닫기**(keymap S3 행 —
    S3L에서 Z=좌 닫기). 2연타 판정과의 충돌은 **"첫 탭 이전 상태(TapStartState)" 기준**으로 푼다:
    닫힘에서 시작한 2연타=도크, 열림에서 시작한 2연타=해제(첫 탭이 이미 닫음 — 재열림 없음).
    문자 키 오토리피트는 기존 `KeyStatus.WasKeyDown` 검사가 그대로 걸러 홀드·2연타 판정을 안 오염시킨다.
  - **셸 구성 상태(S1·S2·S3L·S3R·S3B·S4) 판정 도입**(`MainWindow.ShellState`/`CurrentShellState`) —
    오버레이별 4상태(A58 OverlayState)는 유지하고 그 위의 키 분배 기준. **S4는 enum 자리와 판정 훅
    (`IsOpenFileBrowsing`, 당시 항상 false)만 두었고 → A90(v0.122.0)이 실제 상태로 구현 완료.**
  - **Enter 일괄 토글**(`OnShellEnter`/`BatchToggleOverlays`): S1 = 선택 파일 있으면 열기(중앙 썸네일 →
    좌 리스트 순, 신설 `SelectedFilePath`), 없으면 일괄 토글 / S2 = 일괄 토글(**직전 구성 복원** —
    세션 한정 기억(Q3), 홀드는 반투명 고정으로 승격해 기억, 기억 없으면 A81 기본 세트 좌+우 도크) /
    S3L·S3R·S3B = 일괄 닫기. **원 기능 우선**: 영상 콘텐츠 = 전체화면(액셀러레이터 Handled + 모듈 ID
    이중 가드) · 문서 에디터 줄바꿈(텍스트 입력 통과) · 탐색기 리스트/트리/썸네일 포커스(통과 표면 —
    선택 항목 열기). 구현 결정: keymap 예외가 영상뿐이므로 **이미지·오디오의 Enter=전체화면
    액셀러레이터는 제거**(F11 유지), **빈 영상 모듈은 전체화면 액셀러레이터가 통과**하도록 가드 추가
    (S1 일괄 토글이 살도록 — `VideoPlayerView.OnFullScreenInvoked`).
  - **경계 버튼 신설**(Q7): 좌/우 각각 20×44(높이 44 = 터치 타깃 관례), 중간 높이, 경계선에 10px씩
    걸침(메인을 살짝 덮음), **마우스가 경계선 ±48px 근접 시에만 표시**, 닫힌 상태에서는 창 가장자리가
    그 자리(꺼내기 가능). 동작 = **불투명 도크 토글**. 오버레이보다 위 z순서라 반투명 홀드
    (IsHitTestVisible=false) 중에도 눌린다. S4에서 숨김은 훅으로 준비 → A90(v0.122.0)에서 활성.
  - **포커스 예외**(keymap 확정): 텍스트 입력 = Z·X·Enter·문자 핫키 전부 입력 우선(A32/A34
    `HotkeySupport` 재사용), Esc만 통과 / 리스트·트리·썸네일 = **Z/X는 타이핑 탐색 우선**
    (`ShouldPassThrough` — 키를 삼키지 않고 판정만 리셋).
  - **안전장치**: Alt의 OS 메뉴 모드 회피(down/up 소비) **제거** — 문자 키라 근거 소멸, 고아 심볼 없음
    (grep 확인). 홀드 취소 트리거(다른 키·클릭·휠 개입 — Ctrl+휠 포함)는 **유지**(휠 줌 재정의는 A98 몫).
  - **A92 안내 문구 Z/X 갱신**: "Docked — press Z/X to close" · "Pinned — press Z/X to close"
    (단독 키 닫기 신설을 반영해 "twice" 표현 제거) — FileListOverlay·ContentInfoOverlay 두 벌 동일 갱신.
  - **Esc**: S2 = 전체화면 해제(기존 모듈 액셀러레이터 유지 — 셸 코드 없음) / S3* = 무동작(Q8) /
    S4 = 복귀(→ A90/v0.122.0 구현 완료). 진입 규칙(A81)·3구획 도크(A93)는 변경 없음.

- ※ A90(**'오픈 파일' 버튼 = 자체 탐색기(S4 탐색 모드)**, v0.122.0) 완료 — 결번.
  사양 원본 = `docs/A86-keymap.md`의 "S4 구성 규칙"·키 매트릭스 S4 열(부록 B 49). 확정·구현 내역:
  - **버튼 신설**: 하단 바 시작 메뉴(☰) 버튼 바로 옆(좌 6+36+6=48 지점, 36×36 공통 규격).
    네이티브 파일 대화상자 불사용. **핫키 미배정**(A34 표 확정 뒤 신설된 버튼 — 필요 시 A34 표·
    keymap과 함께 재론), 툴팁 "Open file"(키 표기 없음). **파생 수치: `ModuleBarHost` 좌 Margin
    48 → 90**(A97의 6+36+6에 A90 버튼 36+간격 6 추가 — StartButton·InstanceBadge 여백은 불변).
  - **S4 진입**(S2·S3L·S3R·S3B에서 누름): 이미 떠 있는 구획은 그대로(다시 얹지 않음), 없는 구획만
    **반투명 고정(A58 TranslucentPinned 재사용 — 새 표시 모드 없음, 공간 미차지라 도크 폭·경계
    버튼·열 수 계산 무오염)** 추가, 중앙은 **S4 전용 `ThumbnailExplorer` 인스턴스**(A33 아크릴 배경,
    `UseTranslucentBackground`)로 덮음 — S1 인스턴스와 공유하지 않는다(reparent 함정 회피).
    S4 호스트는 좌/우 패널 폭과 같은 % 스페이서로 스스로 비킨다(반투명 패널은 컬럼 미차지).
    목록 원본 = S1과 같은 좌 리스트 단일 경로(`NavigateList`/`ViewChanged` — S4 중에는 S4 그리드로만
    흐름). 진입 시 좌 리스트는 **현재 콘텐츠 파일의 폴더**로 항해(소실 시 모듈 시작 폴더 폴백),
    포커스는 썸네일 그리드로.
  - **복귀**: Esc·재누름 = 진입 전 좌/우 스냅샷(`_s4Restore` — A86 `_lastBatchStates`와 별개 필드)
    으로 — 이번에 추가된 구획만 내려간다(S4 중 Z/X·경계 버튼 무동작이라 상태 변화 경로가 없어 전체
    대입 = 추가분 되돌리기). **콘텐츠가 바뀌면 자동 종료**(`SetContentState`/`OnContentOpened` —
    더블클릭·Enter·인포 드랍은 물론 숫자 키 모듈 전환·설정 진입도 같은 경로): 스냅샷은 버리고
    좌/우는 A86 "상태는 콘텐츠를 넘어 유지" 규칙의 자연 상태. 새 창 열기(Shift+더블클릭·우클릭)는
    이 창의 콘텐츠가 안 바뀌므로 **S4 유지**(구현 결정).
  - **S1에서 누름** = "이미 열려 있음" 강조만(A90-b): 중앙 썸네일 뷰 둘레 액센트 테두리 450ms —
    Storyboard 불사용(타이머+Visibility 2단계, 실패해도 강조만 안 보임 — A92 선례 반영).
    None(빈 셸·설정·H/W·미지원 안내) = keymap 표 밖, 무동작(구현 결정).
  - **S4 키**(keymap S4 열): Z/X·2연타 = 무동작(`OnRootKeyDown`·`OnOverlaySideDown` 가드) /
    Enter = 선택 열기 우선(`ThumbnailExplorer`가 Enter=선택 항목 열기를 자체 처리 — S1 그리드도
    같은 개선을 받음), 없으면 복귀와 동일 / Esc = 복귀 — 셸이 Esc를 소비하는 유일한 상태
    (`OnShellEscape` — 텍스트 입력 판정보다 먼저: keymap "Esc만 통과") / 경계 버튼 = 표시 안 함
    (A86 훅 활성) / A34 문자 핫키 = 무동작(그리드가 `PassThroughTag` 표면이라 `HotkeySupport`가
    통과 — **코드 추가 없이 기존 구조로 충족**) / 숫자·Shift+N·Ctrl+S = 현행 유지.
  - **영상 전체화면과의 우선순위(구현 결정)**: 모듈 Esc/Enter 액셀러레이터는 셸 루트 핸들러보다
    먼저 돌아 셸이 역전 불가 — S4·전체화면이 겹치면(S4 중 모듈 하단 바 버튼·F11로 진입 가능)
    **첫 Esc = 전체화면 해제, 다음 Esc = S4 복귀** 순서로 정리(등재 당시 "S4 복귀 먼저"를 코드
    현실에 맞게 조정). 영상 **Enter=전체화면은 F11과 핸들러 분리**(`OnFullScreenEnterInvoked`):
    통과 표면 포커스(S4 그리드 포함)에서는 흘린다 — A86 포커스 예외("탐색기 포커스 Enter=선택 항목
    열기 우선")와 정합, F11은 종전대로.
  - 전체화면 중 '오픈 파일': 하단 바가 통째로 숨어(AppWindow.Changed) 버튼을 누를 수 없다 —
    확인만 하고 특별 처리하지 않음(사양 밖).

- ※ A91(**좌 오버레이 주소·필터 줄을 트리 위(패널 최상단)로**, v0.115.0) 완료 — 결번.
  `ExplorerPane.DetachPathBar()`가 경로 바(위로 이동 + 경로 + 필터 + 정렬) 한 줄을 페인에서 떼어 주고,
  `FileListOverlay.Show()`가 리스트를 처음 만들 때 그 줄을 새 최상단 행(`PathBarHost` Border)에 붙인다.
  좌 패널 행 구성 = **Auto(주소 바) / 1*(트리) / 3*(리스트) / Auto(안내 문구)**.
  구현 시 결정: 경로 바를 떼지 않는 다른 사용처(중앙 탐색기·빈 셸 탐색기)는 XAML을 공유하므로
  **Row 0(Auto) RowDefinition은 남겨 둔다**(자식이 없으면 높이 0으로 접힌다).
  중복 부착 방지는 `Children.Contains` + `Border.Child` null 검사 — `FrameworkElement.Parent`는
  라이브 트리 부착 전 null이라 가드로 못 쓴다(v0.111.0 COMException 0x800F1000 전례).
  x:Name 필드는 부모에서 떼어도 살아 있어 `NavigateTo`·A7 필터 코드는 그대로다.

- ※ A92(**도크·고정 안내 문구 일시 표시 후 사라짐**, v0.115.0) 완료 — 결번.
  `FileListOverlay`·`ContentInfoOverlay`의 `SetState`가 안내를 **2.5초 표시 → 300ms 페이드아웃
  (Storyboard + DoubleAnimation Opacity) → Collapsed**로 바꾼다.
  구현 시 결정: **재시작 조건** = 표시 상태로 새로 진입했거나 문구가 바뀐 경우에만(SetState는 상태 머신이
  움직일 때마다 여러 번 불려 매번 되감으면 영영 안 사라진다). 숨겨야 하는 상태(닫힘·도크도 고정도 아님)면
  타이머·애니메이션 즉시 정지 + Collapsed. 다시 띄울 때 `Opacity`를 XAML 기본값 0.6으로 되돌린다.
  공용 헬퍼 없이 두 클래스가 같은 로직을 한 벌씩 갖는다(A93·A86이 곧 이 파일들을 다시 뒤집는다 —
  한쪽을 고치면 다른 쪽도 맞출 것).
  ⚠️ ~~문구 내용은 A86 확정 시 Z/X 기준으로 갱신 대상~~ → **A86(v0.121.0)에서 갱신 완료**
  ("press Z/X to close" — 단독 키 닫기 신설 반영, 두 클래스 동일).

## 3. 이미지 모듈

- ※ A9(하단 바 정보 확충 — 파일명 옆 용량·종류(확장자·비트뎁스)·EXIF 요약(촬영일·카메라·노출) 인라인 표시,
  좁으면 말줄임+전체 툴팁, 조회는 이미지 워커에서, v0.74.0) 완료 — 결번.

## 4. 영상 모듈

- ※ A10(오디오 모듈 분리 ZP-audio — 표시명 Music·단축키 3번 삽입(1이미지 2영상 3오디오 4문서 5압축 6HW)·
  청록 #1FA8A0·음악 확장자 라우팅 이관·파형 시각화 상시 인스턴스 이관·연결 토글·빈 상태 탐색기·앱/파일 아이콘,
  이어듣기 키 audio.resume 분리, v0.75.0) 완료 — 결번.

- ※ A12(재생 시작 시 좌상단 "파일명 · 1080p" 3초 오버레이 — 새 미디어 첫 Playing에서만, 해상도 미파싱 시 파일명만, v0.76.0) 완료 — 결번.

- ※ A13(전체화면 조작 피드백 — 중앙 칩 0.9초: 볼륨 "Volume n%"/뮤트 "Muted"/시킹 "위치/길이"/재생 ▶·❚❚,
  전체화면에서만(창 모드는 슬라이더가 보임), v0.77.0) 완료 — 결번.

- ※ A28([버그] 뮤트/소리 아이콘 반전 — 원인: libvlc Mute 게터가 설정 직후 이전 값 반환(비동기 반영),
  수정: 음소거 상태를 로컬 _muted로 소유·아이콘/A13 피드백도 로컬 기준, 오디오 모듈 사본 동일 수정, v0.78.0) 완료 — 결번.

## 4.1 오디오 모듈

- ※ A66(**샘플 mp3 기본 동봉** — `Assets\sample.mp3`를 Content `CopyToOutputDirectory`로 동봉(비디오
  test-clip.mp4와 동일 패턴), 노출도 비디오와 동일 UX: 빈 상태 ▶ = 샘플 재생 + 플레이스홀더 안내 줄,
  샘플은 이어듣기 제외(IsSampleTrack — 비디오 IsTestClip과 동일).
  내용 확정: 길이 18초, C 메이저 펜타토닉 아르페지오 멜로디(마림바 리드 + 벨 아르페지오 + NES 삼각파
  베이스, 코드 C→Am→F→G→C, 84 BPM) + 페이드 인/아웃 — 자체 생성이라 저작권 무관.
  인코딩: numpy 합성 WAV → ffmpeg libmp3lame 128kbps(약 282KB), 재생성은
  `python3 docs/gen_test_audio.py sample`(ffmpeg 없으면 lameenc 폴백), v0.97.0) 완료 — 결번.

## 6. 문서 모듈 (ZP-doc)

- ※ A16(PDF 뷰어 — OS 내장 Windows.Data.Pdf 렌더(외부 의존성 없음)·.pdf 확장자 라우팅 추가,
  세로 연속 스크롤 + ListView 가상화 지연 렌더(화면 밖 비트맵 해제)·모니터 배율 반영 선명 렌더,
  Ctrl+휠/핀치 줌(비트맵 확대)·하단 바 "현재/전체" 페이지 표시·암호 PDF 입력 후 재시도,
  v0.81.0) 완료 — 결번. HWP·오픈오피스는 A45로 분리.

- ※ A37(텍스트 편집·저장 — 뷰어→에디터 승격(TextBox)·.ini 확장자 추가·Ctrl+S+저장 버튼,
  인코딩(UTF-8/BOM/UTF-16/CP949)·줄바꿈(CRLF/LF) 보존 저장, CP949 불가 문자는 UTF-8 전환 확인,
  수정됨 표시 = 하단 바 "● Unsaved" + 창 제목 ● 접두, 미저장 가드 ICloseGuard —
  다른 파일 열기·모듈 전환·설정 진입·창 닫기(X/Alt+F4/트레이) 시 저장/버리기/취소 확인,
  4MB 초과 잘림 파일은 읽기 전용, v0.80.0) 완료 — 결번. A36 선행 조건 충족.

- ※ A80(문서 뷰어 우측 약 30% 빈 공간 버그 수정 — 원인은 오버레이가 아니라 텍스트 에디터 자체:
  `EditorBox`(v0.44.0 뷰어 TextBlock 유래)가 MaxWidth 900 + 좌측 정렬 고정이라 900px보다 넓은 창에서
  포커스된 TextBox 배경이 좌측 900px에만 그려지고 우측은 창 원배경(검정)이 노출됐다
  (오버레이 AltOverlayRoot/InfoOverlayRoot는 전폭 겹침 + Visibility 토글이라 접힘 시 폭 잔존 없음 확인).
  MaxWidth·좌측 고정을 제거해 본문이 창 전체 폭을 사용. 전 모듈 점검 결과 이미지(중앙 정렬)·
  영상/오디오(전폭 검정 표면)·압축(전폭 리스트)·PDF(PdfPane 중앙 정렬)는 동일 증상 없음, v0.91.0) 완료 — 결번.
  A57(오버레이 공통 모듈화)보다 선행한다는 조건 충족.

## 8. 하드웨어(정보) 모듈

- ※ A20(네트워크 상태 — Network 섹션(Storage와 System 사이): 연결 여부·대표 어댑터(게이트웨이 기준)·
  링크 속도(Mbps/Gbps)·업/다운 전송률(활성 어댑터 합계 2초 차분, "12.3 MB/s ↓ · …↑"),
  NetworkInterface 기반(관리자 불필요)·대시보드 청록 액센트, v0.82.0) 완료 — 결번.

- ※ A39(Always on top — ⛶ 바로 왼쪽 핀(E718) ToggleButton, OverlappedPresenter.IsAlwaysOnTop,
  전체화면 복귀 시 재적용(프레젠터 재생성으로 초기화되는 문제 처리),
  **인포 모듈 전용**(사용자 확정 2026-08-09) — 모듈 전환 등으로 뷰가 내려가면 자동 해제(끌 수단이
  없는 상태 방지), v0.83.0) 완료 — 결번.
  ※ **A61(v0.111.0)이 이 핀의 동작을 확장**했다: 켜면 always on top + **메인 영역을 접어 하단 바만**
  남기고, 끄면 해제 + 접기 전 크기로 복원한다(자동 해제 시 접힘도 함께 풀린다). 위 기록은 그대로 유효하다.

- ※ A75(**정보 화면 컨트롤 정리** — 수동 Refresh 버튼 제거(주기 폴링이 있어 불필요, 사용자 확정):
  `RefreshButton`·`OnRefreshClick`·`HardwareModule.RefreshNow()`·`_forceSpecs` 삭제.
  Busy 링은 **첫 로드(첫 스냅샷 도착 전) 표시만 유지** — 센서 드라이버 로드·첫 WMI 수집이
  1초 이상 걸릴 수 있어 빈 화면 동안의 표시는 필요하다고 판단(수동 Refresh 경로만 제거).
  "Copy all"은 정사각 아이콘 버튼(복사 글리프 E8C8, A27 1칸 규격 40×40 — A97/v0.116.0에서 36×36. ⛶·핀과 동일 계열)
  + 툴팁 "Copy all hardware info and sensor values", 복사 로직은 그대로, v0.93.0) 완료 — 결번.

- ※ A51(**맥박(EKG) 그래프 표시 구간 축소** — 창 길이를 5초 고정에서 **`refreshMs × 2`** 로 교체
  (`HardwareView.PulseWindow`가 `HardwareModule.RefreshMs`를 읽는 계산 프로퍼티), 어느 주기에서도
  스파이크 1~2개만 표시. 주기를 드롭다운(A29)으로 바꾸면 즉시 재렌더(`RerenderPulse`)해 창 길이도
  바로 따라간다. 창 길이가 refreshMs 기준 계산이라 **A73의 50~5000ms 범위에서도 그대로 성립**,
  v0.93.0) 완료 — 결번.
  ※ A73(v0.110.0) 착수 시 재확인: 성립한다. QRS 스파이크 폭(`RenderPulse`의 ±3·±1)은 **ms가 아니라
  픽셀 상수**라 창이 100ms(50ms 주기)든 10초(5000ms 주기)든 90px 안에서 같은 6px 폭으로 그려진다 —
  짧은 창에서 스파이크가 창보다 넓어져 뭉개지는 경우가 없어 **클램프를 추가하지 않았다**
  (클램프를 넣으면 "어느 주기에서도 1~2개"라는 A51의 의도가 깨진다).

- ※ A88(**맥박(EKG) 그래프 연속 스크롤**, v0.114.0) 완료 — 결번. 확정 내역:
  렌더를 데이터에서 분리했다 — 도착 기록(`RecordPulse`의 `_pulseTicks` 추가·정리)은 스냅샷 도착 시에만,
  **그리기는 매 프레임**. `RenderPulse`의 좌표식은 이미 인자 `now` 기준(`start = now - window`)이라
  손대지 않고 호출 빈도만 올렸다 — 그것만으로 스파이크가 우→좌로 흘러간다.
  - **렌더 루프 = `Microsoft.UI.Xaml.Media.CompositionTarget.Rendering`**(디스플레이 주사율 동기,
    `EventHandler<object>`). `Loaded`에서 `-=` 후 `+=`(이중 구독 방지), `Unloaded`에서 반드시 `-=` —
    **static 이벤트라 해제를 빠뜨리면 뷰·창이 통째로 누수**된다.
  - **안 보이면 즉시 return**: 핸들러 첫 줄에서 `PulseHost.Visibility` 검사(A40 폭 축약으로 내려간 상태).
    모듈 전환은 `Unloaded` 해제로 끊긴다. 창 최소화·숨김은 그릴 창이 없으면 프레임이 멈춰 자연히 멎지만,
    같은 UI 스레드에 보이는 다른 창이 있으면 계속 돈다 — 점 10개 미만이라 비용은 무시할 수준.
  - 기록 정리(`RemoveAll`)는 렌더 루프에서 하지 않는다(RecordPulse·RerenderPulse의 몫) —
    창 밖 기록은 `RenderPulse`의 `tick < start` 검사가 건너뛴다. 점 수는 스파이크 1~2개분(10개 미만).
  - A51(창 길이 = refreshMs×2)·QRS 픽셀 폭 상수(±3·±1)는 그대로 — 바뀐 건 "언제 그리느냐"뿐.

## 8.1 All Readable 모듈 (신규)

- ※ A59(**All Readable 통합 모듈 신규** — 모든 지원 형식을 한 창에서 열어보는 모듈, v0.113.0) 완료 — 결번.
  ⚠️ **번호 배정(7번·"1~6 근육기억 유지")은 A96(v0.116.0)이 대체·구현 완료** — All Readable이 **1번**,
  기존 모듈은 순서 그대로 한 칸씩 밀려 이미지 2 ~ 정보 7. 아래 기록의 "번호 7" 언급은 구현 당시 값으로
  읽을 것(현행 번호의 원본은 본문 A34 키 맵 표).
  파일을 열면 **센터와 하단 바만** 그 형식의 모듈 뷰로 교체되고(중첩 호스팅), 창·오버레이·시작 메뉴는
  계속 All Readable의 것이다. 확정·구현 내역:
  - ① **신규 프로젝트 `src/KOTU.Module.AllReadable`** — ID `allreadable`, 표시명 **`All Readable`**,
    브랜드명 `KOTU-all`(KOTU-doc·KOTU-info와 같은 축약 형식), 글리프 `E71D`(AllApps).
    자식 모듈 프로젝트를 참조하지 않는다 — 모듈끼리 직접 참조하지 않는 규칙 그대로이고,
    자식 뷰는 셸이 넘긴 `IModule` 계약(`CreateView`)으로만 만든다.
  - ② **담당 확장자 = 자식 모듈 확장자의 합집합**. 그래서 좌측 리스트 오버레이(A57 ③의 주입 지점)와
    중앙 빈 상태 탐색기 필터가 자동으로 "전 모듈 지원 확장자"가 된다 — 오버레이는 자식이 바뀌어도
    **All Readable이 계속 소유**하고 필터도 그대로 합집합이다(모듈별 필터로 좁아지지 않는다).
    합집합·자식 선택 계산은 UI 비의존이라 Core의 순수 함수(`KOTU.Core.Routing.AllReadableRouting`)로
    두고 단위 테스트했다(`AllReadableRoutingTests` 8건).
  - ③ **등록 순서 = 맨 마지막**(`App.ConfigureServices`). `FileTypeRouter`는 등록 순서가 우선순위라,
    합집합을 가진 이 모듈이 앞에 오면 탐색기 더블클릭이 전부 여기로 빨려 들어간다.
    자식 후보도 "등록된 모듈 − 자기 자신 − 확장자 0개(정보 모듈)"로 뽑아 **중첩 재귀를 원천 차단**했다.
  - ④ ⚠️ **확장자 연결(A38·A35) 대상이 아니다** — 담당 확장자가 자식 모듈과 **전부 겹쳐** 함께
    등록하면 확장자마다 ProgID·UserChoice·Capabilities를 서로 덮어써 어느 모듈이 열릴지 예측할 수
    없게 된다. 그래서 **설정 연결 섹션에 없고**, `ExplorerIntegration`의 등록·재등록·구 브랜드 청소
    대상에서도 빠지며, **파일 아이콘(`Assets/fileicons`)도 만들지 않는다**.
    판단의 단일 소스는 새 계약 속성 `IModule.RegistersFileAssociations`(기본 true, 이 모듈만 false) —
    기존 6개 모듈은 기본 구현을 그대로 물려받아 코드 변경이 없다.
    같은 이유로 **파일 인자로 시작하는 경로(탐색기 더블클릭·연결 프로그램·드래그로 새 창)는
    종전대로 전용 모듈**로 열린다.
  - ⑤ **번호 키 = `7`**, 시작 메뉴 위치는 정보(6)와 Settings(0) 사이. **1~5는 손대지 않았다** —
    사용자 근육기억과 A32·A35(연결 섹션 순서)·문서 문구가 전부 그 순서에 묶여 있다.
  - ⑥ **모듈 액센트 색 = 마젠타 `#C2499A`**(창·트레이 아이콘 `app-allreadable.ico`, "ALL"+kotu 표식).
    기존 6색(amber 37°·green 145°·teal 177°·blue 220°·purple 258°·red 358°)에서 가장 넓게 빈 구간이
    red와 purple 사이라 그 한가운데(320°)를 골랐다 — 어느 색과도 38° 이상 떨어진다.
    A2의 **인스턴스 색 팔레트와는 무관**하다(그쪽은 창 번호 색).
  - ⑦ **A24(새 인스턴스) 규칙**: All Readable 안에서 연 파일은 **All Readable 안에서** 열린다 —
    셸이 새 계약 `IFileOpenTarget`으로 지금 뷰에 먼저 물어보고(`MainWindow.OpenFile`),
    받으면 모듈을 바꾸지 않고 제목·오버레이 기준 파일만 갱신한다. 새 창으로 여는 경로
    (Shift+더블클릭·우클릭 "새 인스턴스"·탐색기 더블클릭)는 새 창에 아직 뷰가 없어 이 분기를
    타지 않는다 = **전용 모듈로 열리는 현행 동작 그대로**. 자식 모듈이 없는 형식(라우팅 재정의로만
    열리는 `.json` 등)은 false를 돌려 기존 라우팅으로 넘긴다("Unsupported file type"보다 낫다).
  - ⑧ **A81(진입 경로별 기본 오버레이) 그대로** — 시작 메뉴에서 실행하면 좌·우 불투명 도크,
    빈 컨텍스트(좌 = 모듈 시작 폴더 `lastFolder.allreadable`/바탕화면, 우 = "No file open").
    셸의 `IsFileModule`에 이 모듈을 더해 빈 상태 탐색기·빈 도크가 성립하게 했다.
  - ⑨ ⚠️ **워커 수명(A42 연장)**: 통합 모듈은 자기 워커를 만들지 않고 자식 뷰의 워커를 쓴다.
    자식 교체와 뷰 Unloaded 양쪽에서 ① 이벤트 구독 해제 → ② **하단 바 조각 제거**(셸 하단 바 트리에
    얹혀 있어 센터를 비워도 남는다) → ③ 센터 비우기(자식 `Unloaded` → 워커 `Dispose`·libvlc 해제·
    하드웨어 구독 해지) 순서로 내린다. ②를 빼먹으면 죽은 자식의 버튼이 하단 바에 남고,
    ③을 빼먹으면 소리가 계속 나거나 파일 핸들이 잠긴다.
  - ⑩ **셸 계약은 전부 자식에게 위임**: `IContentStateSource`(자식이 연 파일 중계 — 자식 내부
    ◀/▶ 탐색도 셸과 동기화) · `IContentInfoProvider`(우측 정보 오버레이가 자식의 EXIF·미디어 정보를
    그대로 받는다) · `ICloseGuard`(문서 자식의 미저장 가드 A37이 셸의 창 제목 ●·닫기까지 이어진다) ·
    `IBottomBarProvider` · `IDriveStripHost`(A22 드라이브 줄은 파일이 없을 때만 뜨므로 자식에게
    넘기지 않고 자체 바가 갖는다). `IWindowCollapseSource`(A61)는 정보 모듈 전용이라 해당 없음.
  - ⑪ **자체 하단 바는 빈 상태 전용**(열기 · 파일명/드라이브 줄 · 전체화면) — 자식이 뜨면 통째로
    숨고 자식 바가 그 자리를 쓴다. **새 핫키를 만들지 않았다**(A34) — 자식 뷰가 자기 버튼 핫키를
    그대로 들고 오기 때문. 예외는 전 모듈 공통인 **F11·Escape(전체화면)**뿐이고, **Enter는 넣지
    않았다** — 문서 편집기의 줄바꿈·리스트 기본 동작을 뺏는다(압축·문서·정보 모듈도 F11/Escape만 쓴다).
  - ⑫ **범위**: 요구대로 센터 + 하단 바 교체까지만. 자체 탭·히스토리·미리보기는 만들지 않았다.

## 9. 설정

- ※ A35(**연결 섹션 순서 변경**, v0.87.0) 완료 — 결번.
  **이미지 → 비디오 → 오디오 → 문서 → 압축** (오디오 위치는 사용자 확정 2026-08-10 —
  시작 메뉴 번호 순서 1이미지 2영상 3오디오 4문서 5압축과 일치). v0.28.0의 "압축→문서→영상→이미지"를 대체.
  우클릭 컨텍스트 메뉴 토글은 압축 모듈 단독이라 기존대로 섹션 맨 아래.
  ※ A59(v0.113.0)의 **All Readable(A96/v0.116.0 이후 번호 1)은 연결 대상이 아니라 이 섹션에 없다** — 담당 확장자가
  다른 모듈과 전부 겹쳐 UserChoice·Capabilities를 서로 덮어쓰기 때문(A59 ④). 순서는 5개 그대로.

- ※ A36(**설정에 "Open settings.json" 버튼** — 설정 파일을 KOTU-doc 에디터로 직접 열기, v0.109.0) 완료 — 결번.
  저장 위치·포맷은 현행 `%AppData%\KOTU\settings.json` 그대로고 **표기만 실제 파일명에 맞췄다**
  (부록 B 37번 확정. 앱 옆 `settings.ini` 이관안은 폐기 — 포터블 지향 요구는 A43에서 따로 다룬다).
  구현 시 결정:
  - 위치 = 연결 섹션 맨 아래(우클릭 메뉴 토글·상태 줄 다음, Updates 머리글 앞). 버튼 규격은 하단 바(A27)가
    아니라 **설정 화면 본문의 기존 버튼("Check now")과 같은 기본 스타일** + 좌측 정렬.
  - 여는 방식 = **새 인스턴스**(`WindowManager.OpenFileInNewWindow`) — 보고 있던 설정 화면을 잃지 않게.
    새 API를 만들지 않고 A24의 "파일 경로로 새 창" 경로를 그대로 탄다.
  - `.json`은 어느 모듈의 `SupportedExtensions`에도 없다(탐색기 연결 대상이 아니다). 그래서 App 시작 시
    **라우팅 재정의 `.json → document`** 한 줄만 추가했다 — 레지스트리 등록 목록·파일 아이콘·A38 대상은 무변경이고,
    부수 효과로 내장 탐색기에서 여는 임의의 `.json`도 문서 에디터로 열린다(기존엔 "Unsupported file type").
  - 경로 문자열은 하드코딩하지 않는다 — `ISettingsService.FilePath`(신설, `JsonSettingsService._path` 노출)에서 읽어
    버튼 아래 한 줄로 표시: `<경로> — changes apply after restart. Editing this file directly can break your settings.`
    (`IsTextSelectionEnabled` — 경로를 복사해 갈 수 있게).
  - 파일이 아직 없으면(설정을 한 번도 저장하지 않은 프로필) 클릭 시 `Save()`로 먼저 만들고 연다.
    그래도 없거나 예외면 버튼 아래 전용 상태 줄에 사유만 남긴다(연결 토글 결과가 쓰는 공용 상태 줄과 분리).
  - **저장 후 자동 재로드는 넣지 않는다**(사용자 확정) — 재시작 반영이며 위 안내 문구가 그 고지다.

- ※ A77(**확장자 연결 토글의 진행 상태·결과 즉시 반영**, v0.106.0) 완료 — 결번.
  레지스트리 작업(등록·해제·A38 UserChoice 쓰기)과 기본 앱 개수 조회를 전부 설정 화면 전용
  `ModuleWorker`(A42 계약)로 옮기고, UI에는 진행률과 결과만 흘린다 — UI 스레드는 더 이상 붙잡히지 않는다.
  확정된 결정(2026-08-12, 사용자):
  - 진행 표기 = **토글 행 우측 `ProgressRing`(16×16) + 상태 텍스트**, 텍스트는 `Registering... (3/12)` /
    `Unregistering... (3/12)` 형태로 **확장자별 n/m 실시간 표시**. A38 UserChoice 단계는 눈에 띄게 걸리므로
    `Setting as default... (3/12)`로 단계 이름을 구분해 보여준다.
  - 작업 중에는 **그 모듈의 토글만** 비활성 + 재진입 차단. 다른 모듈 토글은 그대로 눌린다
    (다만 실행 자체는 워커 큐에서 직렬 — 모듈들이 Capabilities 키 하나를 공유해 동시 쓰기가 위험하다).
  - 진행률 보고 = `IProgress<AssociationProgress>`(Done/Total/Phase) + `DispatcherQueue.TryEnqueue` 마샬링.
    `WorkContext.Progress`는 0..1 double 하나뿐이라 단계 이름을 실을 수 없어 전용 형식을 따로 넘긴다.
  - 완료 후 링을 끄고 상태 텍스트를 지운 뒤 "Default app for n/m extensions"를 갱신한다 —
    **개수 조회는 워커에서, 대입만 UI에서**.
  - 실패 시 동작은 기존대로 **토글 원위치 + 사유 표시**. 다만 **부분 실패(일부 확장자만 실패)는
    토글을 되돌리지 않고** `Registered 10/12 (2 failed)`를 그 행에 남긴다(다음 조작 전까지 유지) +
    기존 A25 폴백(설정 딥링크)도 그대로.
  - 화면을 떠난 뒤 워커가 끝나도 UI를 만지지 않게 `Unloaded`에서 가드 — 진행 중인 레지스트리 작업은
    끊지 않고(반쯤 등록된 상태 방지) 워커가 마저 끝낸다.

- ※ A26(**업데이트 백그라운드 주기 체크 + 윈도우 네이티브 토스트**, v0.105.0) 완료 — 결번.
  ⚠️ **주기 체크·토스트는 A95(v0.117.0)에서 전량 제거됐다 — 지금 앱에 토스트는 없다.**
  당시 구현: 전역 싱글턴 `KOTU.App.Integration.UpdateCoordinator`가 타이머(시작 30초 뒤 첫 체크 → 10분 간격)를
  소유하고 새 버전을 찾으면 `AppNotification` 토스트 → 클릭 시 설정 업데이트 섹션으로 스크롤,
  `update.lastNotifiedVersion`으로 같은 버전 재알림 억제. **현행은 A95를 볼 것.**
  남아 있는 것: 전역 싱글턴 `UpdateCoordinator`(상태 1벌을 여러 창이 공유) · `update.lastCheckError`.

## 10.1 공통

- ※ A22(**드라이브 정보 표시 전면 개편 — no file open 시에만·전체 드라이브·종류(WMI)·사용률 막대·자동 스크롤**,
  v0.108.0) 완료 — 결번.
  확정 사양(사용자): 표시 시점은 **no file open 상태에서만**(v0.47.0의 "파일을 연 후에만"을 뒤집었다.
  파일이 열리면 숨김) / 대상은 **시스템의 모든 드라이브**(준비되지 않은 드라이브는 제외) /
  항목·순서는 **드라이브명 · 종류 · 용량**("사용량 of 전체 (사용률%)" + 채워지는 막대) /
  하단 바 폭을 넘으면 **자동 스크롤 루프**.
  - **구조 = 공용 컨트롤 1개**(`KOTU.App/Controls/DriveStrip.xaml`) + **모듈이 자리를 내주는 슬롯**.
    A57 ②(오버레이를 공용 컨트롤로 추출해 셸이 주입)와 같은 방식이다. 모듈 프로젝트는 셸을
    참조할 수 없으므로(App → 모듈 단방향) 새 계약 `IDriveStripHost`(Core)를 두고 셸이
    `AttachDriveStrip`으로 컨트롤을 끼운다. 인스턴스는 **모듈 뷰마다 새로** 만든다 —
    같은 UIElement를 다른 부모로 옮기면 XAML이 예외를 던진다.
  - **조회는 한 곳뿐**: 드라이브 열거·용량·표기 = Core `DriveStatus.Collect`(구 `Describe` 대체),
    종류 WMI = `KOTU.Module.Hardware.PhysicalDiskKinds`. WMI를 하드웨어 모듈에 둔 이유는
    `System.Management` 참조가 이미 거기에만 있고 셸이 그 모듈을 참조하고 있어서 —
    **새 패키지 참조 없이** 기존 조회 방식을 재사용한다(csproj 무변경).
  - **종류 조회**: `MSFT_Partition`(드라이브 문자 → 디스크 번호) + `MSFT_PhysicalDisk`
    (MediaType 3=HDD·4=SSD·5=SCM / BusType 7=USB·11=SATA·17=NVMe) → `NVMe` · `USB` · `SSD` ·
    `SATA (HDD)` · `HDD`. 실패·미매핑이면 `DriveInfo.DriveType` 근사 표기(Local/Removable/
    Network/CD-ROM), 그마저 모르면 **종류 칸을 통째로 생략**(빈 괄호 금지). 예외는 밖으로 내지 않는다.
  - **캐시·주기**: 종류는 잘 바뀌지 않아 **프로세스 1회 조회 캐시**, 용량·사용률은 `DriveInfo`로
    싸게 얻으므로 **30초 주기 갱신**. 조회는 전용 워커(`KOTU drive strip worker`, BelowNormal)에서만 —
    WMI는 수백 ms~수 초라 UI 스레드에서 돌리면 안 된다.
  - **표시**: `C: SSD 412 GB of 931 GB (44%)` + 오른쪽 막대(60×6, 반지름 3). 사용률 90% 이상은
    테마 경고색(`SystemFillColorCautionBrush`), 미만은 액센트색(`AccentFillColorDefaultBrush`) —
    색은 하드코딩하지 않는다. 드라이브 사이는 간격 + 옅은 세로 구분선. 용량 단위는 1024 기준
    GB 정수, 1 TB 이상은 TB 소수 1자리.
  - **자동 스크롤**: 내용 총 폭 > 표시 영역일 때만. 같은 내용 사본 2벌을 이어 붙여 사본 하나 폭만큼
    왼쪽으로 밀고 반복(마퀴, **초당 30px**) — 이음새가 보이지 않는다. 넘치지 않으면 고정 표시,
    창 크기가 바뀌면 넘침 여부를 다시 판정한다. **숨겨진 동안에는 애니메이션·30초 타이머 모두 정지**
    (뷰 Unloaded에서도 정지).
  - **표시 시점 판단은 셸이** 한다 — 새 상태 플래그를 만들지 않고 이미 있는 `_currentFilePath`
    (SetContentState·OnContentOpened)를 그대로 쓴다.
  - 적용 대상은 v0.47.0에 드라이브 표시가 있던 **이미지·압축·문서** 하단 바. 영상·오디오
    트랜스포트 바는 star 칸을 시크 슬라이더가 쓰고 있어 그대로 뒀다(원래도 드라이브 표시가 없었다).
    압축 바는 상태 문구가 우선 — "Reading archive..." 같은 진행 문구가 있는 동안에는 줄을 내린다.
  - A49의 "좁은 바에서 드라이브 텍스트 숨김"(문서, 임계 760) 규칙은 제거했다 — 드라이브 표시가
    Auto 폭 텍스트에서 남는 폭(star 칸) 슬롯으로 바뀌어 더는 버튼을 밀어내지 않는다.

- ※ A27(**하단 바 버튼 통일 — 테두리·수직 중앙·1칸/2칸 2종 규격**, v0.107.0) 완료 — 결번.
  확정(2026-08-12): **1칸 = 40×40**, **2칸 = 84×40**(1칸 2개 + 간격 4).
  ⚠️ **치수는 A97(v0.116.0)이 개정했다 — 1칸 36×36 · 2칸 84×36 · 간격 6.**
  아래 40×40·84×40 표기는 v0.107.0 당시 값이다. **이 기록만 보고 40으로 되돌리지 말 것.**
  하단 바 두께는 A40의 고정 44로 그때나 지금이나 불변(바 두께는 손대지 않았다).
  구현 = **앱 전역 `Style`을 `App.xaml`의 `Application.Resources`에 정의**하고 모든 하단 바 버튼이
  참조한다(A33의 `OverlayAcrylicBrush` 이관과 같은 방식). 뷰마다 `Width`/`Padding`을 흩뿌리지 않는다.
  - 스타일 키: `BottomBarButtonStyle`(1칸 Button) / `BottomBarWideButtonStyle`(2칸 Button) /
    `BottomBarToggleButtonStyle`(1칸 ToggleButton) / `BottomBarWideDropDownButtonStyle`(2칸 DropDownButton) /
    `BottomBarWideComboBoxStyle`(2칸 ComboBox). WinUI는 `Style`의 `TargetType`이 정확히 일치해야 해서
    타입마다 파생 스타일을 둔다(Button 스타일을 DropDownButton에 못 쓴다). `BasedOn`은 WinUI 기본 스타일
    (`DefaultButtonStyle`·`DefaultToggleButtonStyle`·`DefaultDropDownButtonStyle`·`DefaultComboBoxStyle`) —
    생략하면 기본 ControlTemplate을 잃을 수 있다.
  - 공통 Setter: `Width`/`Height`(규격) · `Padding 4` · `BorderThickness 1` · `CornerRadius 4` ·
    `VerticalAlignment Center` · 콘텐츠 정렬 중앙. **색은 지정하지 않는다** — 시스템 기본 Button 룩
    (테마 리소스)을 그대로 쓴다. 통일이지 재디자인이 아니다.
  - **SplitButton(Fit)만 예외**: WinUI가 키 있는 기본 SplitButton 스타일을 노출하지 않아 `BasedOn` 대상이
    없다. 전역 스타일 대신 같은 2칸 값(84×40 + 수직 중앙 + CornerRadius 4)을 뷰에 직접 적었다
    (3곳 = 이미지·문서·영상. **A97/v0.116.0에서 세 곳 모두 84×36**).

  | 하단 바 | 1칸(40×40 → A97에서 36×36) | 2칸(84×40 → A97에서 84×36) | 손대지 않은 것 |
  | --- | --- | --- | --- |
  | 셸(MainWindow) | 메뉴(☰) — 투명 배경·테두리 0 예외 제거 | — | 인스턴스 배지, 모드 칩 |
  | 이미지 | 열기 · Rotate · 1:1 · ⛶ | Fit(SplitButton) | 파일명·메타·드라이브 텍스트 |
  | 영상 | 열기 · 재생 · 음소거 · 자막 · 1:1 · ⛶ | 배속(ComboBox) · Fit(SplitButton) | 시크/볼륨 슬라이더, 시간 표시 |
  | 오디오 | 열기 · 재생 · 음소거 · ⛶ | 배속(ComboBox) | 시크/볼륨 슬라이더, 시간 표시 |
  | 문서 | 열기 · 저장 · 1:1 · ⛶ | Fit(SplitButton) | 파일명·수정됨·페이지·드라이브 텍스트 |
  | 압축 | 열기 · ⛶ | Cancel(1칸에 안 들어가는 텍스트) | 상태 텍스트, 진행 막대 |
  | 정보(H/W) | Copy all · Always on top(ToggleButton) · ⛶ | 리프레시 주기(DropDownButton) | 센서 카드·맥박 그래프(A60·A71·A72에서 개편 예정) |
  | 설정 | ⛶ | — | Patreon 링크(HyperlinkButton — 버튼 규격이 아니라 텍스트 링크) |

  - 대상은 **하단 바(작업표시줄 한 줄)에 얹히는 것만** — 뷰 본문·대화상자·설정 본문 버튼
    (압축 Extract 줄, 탐색기 툴바, 비관리자 안내의 Restart as admin 등)은 그대로 둔다.
  - 텍스트·툴팁·동작·핫키는 무변경. 이미지 하단 바의 배경·패딩은 이미 `TakeBottomBar()`가
    다른 모듈과 같은 값으로 맞추고 있어 XAML은 손대지 않았다.
  - 폭 영향: 영상 바 고정 요소 합이 약 +22px(배속 60→84가 대부분) — A40의 좁은 바 축약
    (760px 미만에서 볼륨 슬라이더·시간 텍스트 숨김, 약 156px 확보)이 그대로 흡수한다.
    문서 바는 Fit 90→84로 6px 줄어 A40 임계값 760 유지.

- ※ A84(**전역 단축키 Ctrl 계열 → Shift 계열 전환 — 유일한 예외 Ctrl+S**, v0.102.0) 완료 — 결번.
  부록 B 31번(2026-08-12 사용자 확정) 기준 구현. 코드의 Ctrl 조합 전수 목록과 전환 표:

  | 지점 | 기존 | 변경 | 처리 |
  | --- | --- | --- | --- |
  | 새 창(A24, `MainWindow.RegisterShortcuts`) | Ctrl+N | **Shift+N** | 텍스트 입력 포커스 시 통과(대문자 N 입력 우선 — A32 통과 규칙을 Shift 조합까지 확장) |
  | 이미지 뷰어 줌(ScrollViewer 내장) | Ctrl+휠 | **Shift+휠** | 콘텐츠 프레젠터에서 휠을 가로채 내장 Ctrl+휠 줌 차단 + Shift+휠 수동 줌(포인터 고정점, 노치당 10%, 범위 10%~800% 유지). 핀치 줌은 그대로 |
  | PDF 뷰어 줌(A16/A49, ListView 내장 ScrollViewer) | Ctrl+휠 | **Shift+휠** | 이미지와 동일 방식. 수동 줌 시 Fit 추종 해제(A49 규칙)는 명시 호출로 유지 |
  | 문서 저장(A37, `DocumentView`) | Ctrl+S | **유지** | 에디터에서 Shift+글자 = 대문자 입력이라 Shift 조합 성립 불가 — 앱에 남는 유일한 Ctrl 조합(사용자 확정) |
  | Ctrl+숫자·Ctrl+` 별칭 | 잔존 없음 | (해당 없음) | A32(v0.66.0)가 이미 단독 키로 전환 — grep으로 잔존 별칭 없음 확인, 제거할 코드 없음 |

  - **뷰어 콘텐츠 위에서만** Shift+휠 줌 — 리스트/그리드 위 무동작(가로 스크롤 관습 회피)은
    핸들러를 각 뷰어의 ScrollContentPresenter에만 배선해 구조적으로 보장.
  - **오버레이 홀드와의 간섭**(A58): Shift+N은 기존 "다른 키 개입 시 홀드 취소"가 방어.
    Shift+휠은 KeyDown이 아니어서 **휠(PointerWheelChanged)을 홀드 취소 트리거에 추가** —
    클릭 개입과 같은 규칙, 오버레이 자신 안에서의 스크롤은 예외(A58 전이 로직 자체는 무변경).
    ※ A86(v0.121.0)부터 오버레이 키가 Z/X라 **Shift 조합과 오버레이의 간섭 자체가 소멸** —
    휠 포함 홀드 취소 트리거는 일반 안전장치로 그대로 유지(Ctrl+휠 개입도 취소로 취급).
  - 표기 일괄 갱신: 설정 안내문(Ctrl+N→Shift+N)·랜딩 페이지(Ctrl+wheel→Shift+wheel)·관련 주석.
    Ctrl+S 표기(툴팁·문서 모듈 안내문)는 유지.
  - A41(UI 배율)의 휠 줌은 미구현 — 구현 시 Shift+휠 기준을 따른다.
  ※ **2026-08-13 A98(v0.123.0)이 이 표의 휠 항목을 대체 완료**(Ctrl+휠 복귀 + 사진 휠 단독 줌).
  Shift+N·Ctrl+S 항목은 그대로 유효.

- ※ A67(**시작 메뉴 스폰서 이미지 클릭 → 링크 열기**, v0.109.0) 완료 — 결번.
  매핑은 후보 ②안 채택 — **`Assets/sponsors.json`**(csproj `Content` + `CopyToOutputDirectory`,
  sample.mp3·test-clip.mp4와 같은 동봉 패턴). 스키마 `[{ "file", "name", "url" }]`, 매칭은 **파일명 대소문자 무시**.
  동봉본 sponsor-1~4.png 4개 항목은 **v0.115.0에서 채웠다** — 4건 모두
  `name` = `KOTU releases`(툴팁 문구로 쓰인다), `url` = `https://github.com/zpstudios/kotu/releases`(사용자 확정).
  파일만 고치면 되는 설계라 **코드 변경 없음**(`SponsorAds.LoadLinks()`가 실행 시점에 읽는다).
  구현 시 결정:
  - `SponsorAds.CurrentLink()`가 현재 분(minute) 시드의 이미지에 대응하는 `SponsorLink(Url, Name)`를 준다.
    파일이 없거나 깨졌으면 조용히 링크 없음(광고 때문에 앱이 죽으면 안 된다는 기존 try/catch 관례 유지).
    손으로 고치는 파일이라 주석·후행 쉼표는 허용해 읽는다.
  - **링크 없음** = 커서 기본·툴팁 없음·클릭 무반응(현행 유지). **링크 있음** = 커서 Hand,
    툴팁은 `name`이 있으면 `name`, 없으면 URL 호스트, 클릭 시 `Windows.System.Launcher.LaunchUriAsync`로
    기본 브라우저에서 열고 **시작 메뉴를 닫는다**.
  - **확인 창 없음**(미션 문구 "silent in-app ad"와 정합). 대신 매핑을 읽을 때 **http/https 스킴만 통과**시켜
    그 외 스킴은 링크로 인정하지 않는다(안전장치).
  - 커서: WinUI 3에는 공개 커서 속성이 없고 `UIElement.ProtectedCursor`가 protected라, 광고 카드 호스트를
    `Grid` 파생(`MainWindow.SponsorCard`)으로 바꿔 `InputSystemCursor.Create(Hand/Arrow)`를 건다.
    비주얼 트리에 붙기 전에는 지정할 수 없어 로드 전 요청은 예약만 하고 `Loaded`에서 적용한다.
    커서·툴팁·클릭은 이미지가 아니라 **카드**가 받는다 — SPONSOR 배지 위에서도 같게 동작한다.
  - 적용 위치: 광고 표시 위치는 **시작 메뉴 카드 하나뿐**이다. 설정 하단 바의 광고는 v0.52.0에 Patreon 문구로
    대체됐고(부록 B 5번 "광고 = 시작 메뉴 위치가 전부") `SponsorAds` 사용처도 그 한 곳이다.
