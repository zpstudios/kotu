# A48 조사 — v0.209.0 시점, 단일 원본

> 과제: 윈도우 디스플레이 배율을 앱에서 직접 변경(A21/A44 확장 — 현재는 조회·표시만).
> 등재문 3안(① 비공식 DisplayConfig API / ② 레지스트리 DpiValue / ③ ms-settings 딥링크)의
> 실태 조사와 권고. 조사 전용 — 코드 변경 없음. 확실하지 않은 판단은 "추정"으로 표기.

## 1. 현행 조회 코드 정독 (A21/A44)

- 조회 경로는 **`XamlRoot.RasterizationScale` 단 하나**다.
  - `src/KOTU.App/SettingsView.xaml.cs:608` — "XamlRoot.RasterizationScale = 이 창이 떠 있는
    모니터의 시스템 배율(앱 자체 배율과 무관)" 주석.
  - `SettingsView.xaml.cs:625-645` `UpdateWindowsScaleMark()` — `:628`에서
    `winPercent = (int)Math.Round(xr.RasterizationScale * 100)`을 구해, `:630-639`에서
    UI scale 콤보 항목 옆 "(current Windows setting)" 표기 또는 "not in the list above" 안내 줄을
    갱신한다. `:645`에서 `xr.Changed` 구독으로 모니터 이동·배율 변경을 추종한다(A21의 "모니터별·
    변경 추종"의 실체).
- 배율 목록은 `src/KOTU.App/UiScale.cs:15` `Percents = [100,125,150,175,200,225,250,300,350]`
  ("윈도우 디스플레이 설정이 제공하는 배율 목록과 동일하게 유지" — `:14` 주석).
- **모니터 식별자는 갖고 있지 않다.** 저장소 전체에서 `GetDpiForMonitor`·`MonitorFromWindow`·
  `EnumDisplayDevices`·EDID·`QueryDisplayConfig` 사용례 0건(grep 확인). RasterizationScale은
  "지금 이 창이 놓인 모니터의 배율"이라는 스칼라 값일 뿐, ②의 `{모니터}` 레지스트리 키 이름을
  구성할 재료(제조사/제품 코드/시리얼)가 아니다. **②·① 어느 쪽이든 모니터 열거·식별 코드는
  전량 신규 작성**이 된다(①은 `QueryDisplayConfig`의 adapterID+sourceID로 충분해 EDID 파싱은
  불필요 — §2).
- 참고: 배율 변경이 실제로 일어나면 앱 쪽은 이미 대응한다 — `src/KOTU.App/MainWindow.xaml.cs:250`
  `xr.Changed += (_, _) => ApplyUiScale();` (A41이 시스템 DPI 변화에 맞춰 상대 배율을 재적용).

## 2. ①안 실태 — 비공식 DisplayConfig DPI API

**정확한 API 이름**: `DisplayConfigSetDeviceInfo` / `DisplayConfigGetDeviceInfo`(winuser.h, 공개·문서화).
등재문의 "SetDisplayConfigDeviceInfo"는 오기다(§5 어긋남 항목). 비공식인 것은 함수가 아니라
**type 값과 그에 따른 구조체**다: 문서화된 `DISPLAYCONFIG_DEVICE_INFO_TYPE`은 음수가 없는데,
설정 앱(immersive control panel)·user32.dll 리버싱 결과 **GET = -3, SET = -4**를 쓴다.
(출처: lihas/windows-DPI-scaling-sample README — https://github.com/lihas/windows-DPI-scaling-sample ,
MS 공식 enum — https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ne-wingdi-displayconfig_device_info_type )

**값 인코딩 — 권장 배율 대비 상대 인덱스** (imniko/SetDPI `DpiHelper.h` 원문 확인 —
https://github.com/imniko/SetDPI/blob/master/DpiHelper.h ):
- 배율 후보 표는 `DpiVals[] = {100,125,150,175,200,225,250,300,350,400,450,500}` — KOTU의
  `UiScale.Percents`(350까지)와 같은 계열의 상위 호환.
- GET(-3) 구조체 `DISPLAYCONFIG_SOURCE_DPI_SCALE_GET`: `minScaleRel`/`curScaleRel`/`maxScaleRel`
  (int32) — 전부 **권장(recommended) 배율로부터의 스텝 수**. 예: 권장 175%에서 `curScaleRel=-1`
  이면 현재 150%. min은 항상 100%이므로 `minScaleRel`로 권장값을 역산한다.
- SET(-4) 구조체 `DISPLAYCONFIG_SOURCE_DPI_SCALE_SET`: `scaleRel` int32 하나 — 권장 대비 상대
  스텝. **프리셋 값만 가능, 커스텀 %는 불가**(imniko/SetDPI issue #5 —
  https://github.com/imniko/SetDPI/issues/5 ).

**per-monitor 지정**: `QueryDisplayConfig()`로 path를 얻어 **adapterID(LUID) + sourceID** 쌍을
헤더에 넣는다. DPI 배율은 target(모니터)이 아니라 **source 속성**이다(lihas README 명시).
멀티 모니터에서 창이 놓인 모니터를 고르려면 창의 HMONITOR → 해당 source 매핑이 필요하다.

**즉시 적용 여부와 앱 자신의 반응**: 즉시 적용된다(설정 앱이 쓰는 바로 그 경로). OS가
`WM_DPICHANGED`를 보내고, WinUI에서는 `XamlRoot.Changed`가 발화 → KOTU는
`MainWindow.xaml.cs:250`·`SettingsView.xaml.cs:645` 구독이 이미 있어 **A41 재적용과
"(current Windows setting)" 표기 갱신이 자동으로 따라온다**. 별도 신규 배선 불요(추정 아님 —
구독 코드 실재; 다만 실기기 확인 포인트로 남길 것).

**최신 동작 상태(Win11 24H2/25H2)**: imniko/SetDPI·lihas 저장소 이슈 트래커에 **24H2/25H2에서
깨졌다는 보고가 없다**(2022년 issue #5 "커스텀 배율 미지원"이 최신 기능성 이슈). 활발히
유지되는 포크 lesferch/SetDPI(1.1.0)가 현재도 배포 중이고 Win11에서 동작을 전제로 문서화돼
있다( https://lesferch.github.io/SetDPI/ ). **"24H2/25H2에서 살아 있다"는 직접 실측이 아니라
"깨짐 보고 부재 + 활성 포크" 기반 추정**이다 — 설정 앱 자신이 같은 경로를 쓰는 한 유지될
공산이 크다(추정).

**실패 모드**: ⓐ API가 에러 반환(권한·유효하지 않은 source) ⓑ min/max 범위 밖 요청 → 실패
또는 클램프 ⓒ 미래 OS에서 type -3/-4 제거·구조체 변경 → 호출 실패. 전부 반환값으로 감지
가능한 부류라 try/catch + 반환값 검사 + ③ 폴백으로 안전하게 감쌀 수 있다. A166의 UCPD처럼
**커널이 능동적으로 되돌리는 보호는 없다**(이 차이가 §4 판정의 근거).

**보조 경로(참고)**: 주 모니터 한정이면 `SystemParametersInfo(SPI_SETLOGICALDPIOVERRIDE)`도
있다(lihas README Update 1) — 단순하지만 멀티 모니터 지정·현재값 조회 불가. 채택 불요(추정:
①이 이를 포괄).

## 3. ②안 실태 — 레지스트리 `PerMonitorSettings\{모니터}` `DpiValue`

- **값 의미**: ①과 같은 축 — **권장 배율 대비 상대 스텝**을 DWORD 2의 보수로 기록
  (0 = 권장, `FFFFFFFE` = -2스텝 등). 같은 0이라도 외장 모니터(권장 100%)와 노트북 패널
  (권장 150%)에서 실제 %가 다르다는 커뮤니티 실측이 상대 인코딩을 뒷받침한다.
  (출처: AutoHotkey 포럼 실측 스레드 — https://www.autohotkey.com/boards/viewtopic.php?t=34701 ,
  elevenforum 튜토리얼 — https://www.elevenforum.com/t/change-display-dpi-scaling-level-in-windows-11.934/ )
- **즉시 적용 안 됨**: 레지스트리 기록만으로는 반영되지 않고 **로그오프/재로그인 필요**가
  중론. `WM_SETTINGCHANGE` 브로드캐스트로 즉시 반영된다는 신뢰할 만한 보고는 **찾지 못했다**.
  AutoHotkey 스레드는 explorer 재시작으로도 안 되고 Win+P 시퀀스 같은 우회만 부분적으로
  통한다고 보고한다(불안정 — 채택 부적합).
- **모니터 키 이름 구성**: 예 `LEN41410_00_07E3_24^A8DD7E34BCF1555F032E26E990ABC597` —
  EDID(제조사+제품+제조년 등) 앞부분 + **알고리즘이 완전히 규명되지 않은 해시** 뒷부분.
  lihas가 SO 답( https://stackoverflow.com/a/57397039/981766 )으로 부분 해석을 시도했으나
  "not perfect"이며, 실용 해법은 **또 다른 비공식 type `DisplayConfigGetDeviceInfo(-7)`**로
  모니터 고유 ID 문자열을 직접 얻는 것이다(lihas README Update 3). 즉 **②를 하려 해도 비공식
  API 의존이 없어지지 않는다.**
- MS 공식 문서는 이 키를 "OS가 배율을 저장하는 위치"로만 언급한다(쓰기 API로 안내하지 않음 —
  https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/dpi-related-apis-and-registry-settings?view=windows-11 ).
- **①과의 조합 가능성**: 불요. ①로 설정하면 OS(설정 경로)가 이 키에 스스로 영속화한다 —
  ②는 ①의 저장소를 손으로 만지는 하위 호환일 뿐, ①이 되는 환경에서 ②를 병행할 이유가 없다.
  ①이 미래 OS에서 막혔을 때의 대안으로도 "재로그인 필요"라 UX가 성립하지 않는다.

## 4. 저장소 관례와의 정합 — 어느 선례에 가까운가

| 선례 | 성격 | 결말 |
|---|---|---|
| A164 `IPolicyConfig` (v0.183.0) | 비공개 COM이지만 OS 자신이 쓰는 경로, 능동 차단 없음 | **try/catch + 폴백으로 채택** — `src/KOTU.App/Integration/DefaultAudioInput.cs:6-11,33,72-80` |
| A38→A166 UserChoice/UCPD (v0.184.0) | 커널 필터(UCPD.sys)가 쓰기를 **능동 차단·원복** | **불가 판정 → 폴백 종결** — `src/KOTU.App/Integration/ExplorerIntegration.cs:203-244` |

**판정: ①안은 A164(IPolicyConfig) 선례에 가깝다.** 공통점 — ⓐ 문서화되지 않았지만 OS 설정
앱 자신이 쓰는 경로 ⓑ 차단 기제가 없고 실패가 반환값으로 드러남 ⓒ 실패 시 사용자 안내 +
폴백으로 수습 가능. A166형(원리적 불가)이 아니므로 "조사 후 포기"가 아니라 "격리 파일 +
try/catch + 폴백"의 기존 패턴으로 채택할 수 있다. 폴백 ③의 선례도 이미 있다 —
`ExplorerIntegration.cs:157`의 `ms-settings:defaultapps` 딥링크(A25류)와 동형인
`ms-settings:display`.

## 5. 권고안

### 3안 비교

| | ① DisplayConfig(-4) | ② 레지스트리 DpiValue | ③ ms-settings:display 딥링크 |
|---|---|---|---|
| 즉시 적용 | **O** (설정 앱과 동일 경로) | X — 재로그인 필요(중론) | 사용자가 직접 조작 |
| per-monitor | O (adapterID+sourceID) | O (키가 모니터별) | 설정 앱이 알아서 |
| 비공식 의존 | type -3/-4 + 구조체 | 키 해시 or type -7 — **②도 비공식 의존** | 없음 |
| 실패 감지 | 반환값으로 즉시 | 적용 여부 확인 자체가 곤란 | 해당 없음 |
| 선례 정합 | A164형(채택 가능) | 선례 없음 + UX 불성립 | A25형(기존 패턴) |

**권고: ① 주 경로 + ③ 폴백. ②는 제외.** ①은 A164 관례(App 셸 격리 파일, try/catch, 실패 시
폴백)로 구현하고, 실패·미지원 시 ③으로 자동 전환. 적용 대상은 "설정 창이 떠 있는 모니터"를
기본으로 하되(§1의 RasterizationScale 표기와 축이 일치), 모니터 선택 UI는 구현 시 결정 사항.

### A41(KOTU 자체 UiScale, v0.209.0 라이브 조절)과의 관계 — 중복 아님, 보완

- A41은 **KOTU 창만** 시스템 DPI 대비 상대 ScaleTransform으로 그린다(`UiScale.cs:4-7`,
  `MainWindow.xaml.cs:354-364`). A48은 **OS 전체 배율**을 바꾼다 — 모든 앱에 영향.
- 상호작용도 이미 안전하다: A48로 시스템 배율이 바뀌면 `RasterizationScale`이 변하고
  `xr.Changed → ApplyUiScale`(`MainWindow.xaml.cs:250`)이 상대 배율을 재계산하므로, A41
  오버라이드를 켠 사용자에게는 KOTU가 선택 배율을 유지한 채 다른 앱만 커진다(의도된 동작).
- 설정 화면에서는 기존 Display 섹션(같은 배율 목록·같은 "(current Windows setting)" 표기)에
  나란히 두되 "이 앱만 / Windows 전체" 구분 문구 필수(등재문 재확인).

### 택일 질문 문안 (부록 B 등재용)

> **A48 방식 택일**: ①(비공식 `DisplayConfigSetDeviceInfo` type -4 — 즉시 적용, A164
> IPolicyConfig와 같은 try/catch+격리 패턴) + 실패 시 ③(`ms-settings:display` 딥링크) 자동
> 폴백 조합으로 확정할까요? ②(레지스트리 DpiValue)는 재로그인 필요 + 키 해시 비공개로 제외
> 권고입니다. 부속 결정 1건: 적용 대상은 "설정 창이 있는 모니터" 고정으로 할지, 모니터 선택
> 콤보를 둘지.

### 등재문 전제와 코드·실태의 어긋남

1. **API 이름 오기**: 등재문의 "SetDisplayConfigDeviceInfo" → 실제는 `DisplayConfigSetDeviceInfo`.
2. **"모니터 식별자 재사용" 전제 불성립**: A21/A44는 `XamlRoot.RasterizationScale` 스칼라만
   쓴다(§1) — ②의 `{모니터}` 키를 만들 식별자를 현행 코드는 갖고 있지 않다.
3. **② "재로그인 필요한 경우가 많음"은 과소평가**: 즉시 반영 신뢰 경로가 사실상 없고, 키 이름
   해시 미규명으로 ②도 비공식 API(-7)에 의존한다 — 등재문이 암시하는 "①의 안전한 대체재"가
   아니다.
