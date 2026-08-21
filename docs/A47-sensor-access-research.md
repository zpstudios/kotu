# A47 조사 — v0.209.0 시점, 단일 원본

> 과제: "Restart as admin" 없이 센서 읽기 — 등재문 3안(① 드라이버 서비스화 / ② 비관리자 소스 대체 /
> ③ requireAdministrator)의 실현성 판정과 택일 자료. 조사 전용 배치(코드 변경 없음).
> 판정 근거 = 실코드(파일:라인) + 웹 출처(URL). 확인 못 한 것은 "추정"으로 표기.

---

## 0. 요약(3줄)

1. **등재문 ①의 전제가 소멸했다** — KOTU가 쓰는 LibreHardwareMonitorLib 0.9.6에는 WinRing0가 아예 없다
   (0.9.5에서 제거·PawnIO로 교체, WinRing0는 2025-03부터 MS 취약 드라이버 차단 대상). PawnIO는 **별도
   설치형**이라, PawnIO 미설치 머신에서는 **Restart as admin을 눌러도 CPU 온도·전력·클럭·팬이 계속 null**이다(§1.4).
2. ①의 현대적 실현형은 "커널 드라이버를 비관리자에 개방"이 아니라(그 구성이 바로 CVE-2020-14979) **승격
   수집 서비스 + IPC**다 — 선례 존재, 그러나 Velopack에 서비스 설치 지원이 없어 배포·수명 관리 비용이 크다(§2).
3. ②로 실제 회수 가능한 것은 **CPU 클럭 근사(PerfCounter)와 (추정) NVMe SSD 온도** 2개뿐 — CPU 온도·전력·팬은
   커널 접근 없이는 구조적으로 불가, MSAcpi WMI는 실효성 없음(§3). 권고 = ② 축소판 선행 + 수요 시 ①(서비스형) 후속(§4).

---

## 1. 현행 구조 정독

### 1.1 패키지·초기화 경로

- 패키지: `LibreHardwareMonitorLib` **0.9.6** + `System.Management` 10.0.*
  (`src/KOTU.Module.Hardware/KOTU.Module.Hardware.csproj:16-19`). 도입 시점 = v0.63.0/A17,
  **2026-08-07 커밋 `a548479`** — 즉 KOTU는 처음부터 PawnIO 시대 라이브러리만 썼고 WinRing0 시대를 거친 적이 없다.
- 열기: `SensorService.EnsureOpen()`(`src/KOTU.Module.Hardware/SensorService.cs:149-173`)이 첫 `Read()`에서
  `Computer { IsCpuEnabled, IsGpuEnabled, IsMemoryEnabled, IsMotherboardEnabled, IsStorageEnabled }`를 만들고
  `computer.Open()`. 실패 시 `_openFailed = true`로 영구 저하(166행 주석 "드라이버 로드 불가 등").
- 승격 판정: `SensorService.IsElevated`(72-85행, `WindowsPrincipal.IsInRole(Administrator)`).
- 저하 UI: `HardwareView.xaml.cs:1079-1083` — `!IsElevated`이고 CpuTemp/CpuPower/FanRpm/SsdTemp 중 하나라도
  null이면 AdminRow 표시. 버튼(`HardwareView.xaml:54` "Restart as admin") → `OnElevateClick`(1247-1248행) →
  `AdminRelaunchHook.Relaunch(SensorService.Shutdown)`(runas 재실행, A17/A94/A124).

### 1.2 저하 모드가 실제로 잃는 것 (코드 주석 기준, SensorService.cs:28-29)

| 채널(SensorFrame) | 소스 | 비관리자 | 근거 |
|---|---|---|---|
| CpuTemp | CPU MSR/레지스터 (PawnIO 모듈) | **null** | SensorService.cs:28 |
| CpuPower | CPU MSR (RAPL 등) | **null** | 〃 |
| CpuClock | CPU MSR | **null** | 〃 ("클럭(MSR)") |
| FanRpm | SuperIO(메인보드)·EC·쿨러 | **null** | 〃 ("팬(SuperIO)"), 160행 "SuperIO(팬) — 관리자 필요" |
| SsdTemp | SMART/NVMe 질의 | **null** | 〃, 161행 "SMART 온도 — 관리자 필요" |
| CpuLoad | OS 성능 데이터 | 나옴 | 29행 |
| GpuTemp / GpuPower / GpuLoad | 벤더 API(NVAPI/ADL/IGCL) | 나옴 | 29행 "GPU(벤더 API)" |
| RamLoad | OS 메모리 통계 | 나옴 | 29행 |

즉 10채널 중 **5개가 승격 요구, 5개는 비관리자에서도 나온다.**

### 1.3 Ring0 초기화의 실체 (라이브러리 내부 — 0.9.5에서 전면 교체)

- LHM **v0.9.5**에서 "Swap WinRing0 to PawnIO"(PR #1857)와 "Use PawnIO driver directly in
  LibreHardwareMonitorLib"(PR #1908)가 들어갔고, #1908의 마지막 패치가 **`KernelDriver.cs`(WinRing0
  서비스 설치/삭제 코드)를 통째로 삭제**했다. 0.9.6은 "Update PawnIO modules to 2.2"(#2174),
  "Fix for new PawnIO release + new installer"(#2222)로 그 위에 얹힌 버전(2026-02-14 릴리스).
  - 출처: https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/releases/tag/v0.9.5 ·
    https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/releases/tag/v0.9.6 ·
    https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/pull/1908 (패치 원문 확인)
- 0.9.6의 실제 열기 경로(#1908 패치 원문): `PawnIo.LoadModuleFromResource()`가
  `CreateFile(@"\\.\PawnIO", ReadWrite, ...)`로 **이미 설치돼 있는 PawnIO 드라이버 디바이스**를 열고,
  라이브러리에 내장된 서명 모듈(.bin)을 `IOCTL_PIO_LOAD_BINARY`로 로드한다. 디바이스가 안 열리면
  **예외 없이 `PawnIo(null)`을 돌려주고 Execute가 빈 배열 반환** → 해당 센서들만 조용히 없다.
  설치 감지는 레지스트리 `HKLM\...\Uninstall\PawnIO`.
- PawnIO 드라이버(namazso 작, LHM·FanControl 진영 공동 채택)는 **별도 설치본**(PawnIO.Setup)이고, 서명
  모듈만 실행하는 커널 VM이다. **디바이스 열기는 기본 관리자 전용**이며, 2.x부터 "비관리자에 개방하는
  옵션이 있으나 비권장"이라고 명시한다. 출처: https://pawnio.eu/ · https://github.com/namazso/PawnIO ·
  https://github.com/namazso/PawnIO.Setup/releases

### 1.4 등재문·주석과 실물의 어긋남 (이번 조사의 최대 발견)

1. **A47 등재문 ①의 "LHM의 커널 드라이버(WinRing0)"는 현물과 다르다**(REQUIREMENTS.md:1475) — 0.9.6에
   WinRing0는 없고 PawnIO다.
2. **csproj 주석 "커널 드라이버를 동봉·자동 로드"(KOTU.Module.Hardware.csproj:17-18)와 SensorService 주석
   "관리자 권한이 없으면 커널 드라이버를 못 올려"(SensorService.cs:28)는 WinRing0 시대 서술** — 0.9.6은
   드라이버를 동봉하지 않으며 스스로 올리지도 않는다. THIRD-PARTY-NOTICES.md:19의 "커널 드라이버
   (WinRing0/PawnIO)를 동봉·자동 로드"도 절반이 틀렸다(동봉되는 건 PawnIO용 **모듈**뿐, 드라이버 본체 아님).
3. **행동적 함의**: PawnIO가 설치돼 있지 않은 머신(KOTU 설치본은 PawnIO를 안 깐다 — release.yml 전체에
   드라이버 단계 없음)에서는 **Restart as admin 후에도 CpuTemp/CpuPower/CpuClock/FanRpm이 계속 null**이고,
   이때 `IsElevated == true`라 AdminRow 휴리스틱(HardwareView.xaml.cs:1078 주석 "관리자인데도 비면
   하드웨어가 그 값을 안 주는 것 — 버튼을 내밀지 않는다")이 **침묵 실패로 오분류**한다. SsdTemp만은
   PawnIO와 무관한 디스크 핸들 직접 질의(DiskInfoToolkit 경로)라 승격만으로 회복될 가능성이 높다(추정 —
   실기기 확인 포인트). 개발 머신에서 승격 시 전 채널이 나왔다면 그 머신에 PawnIO가 이미 설치돼 있었을
   가능성이 크다(FanControl/LHM 앱 등이 깔았을 수 있음 — 확인 필요).

---

## 2. ①안 실현성 — "드라이버를 1회 설치하고 비관리자로 읽기"

### 2.1 등재문 원안(WinRing0 서비스 상주 + 비관리자 개방)은 폐기해야 한다

- WinRing0는 2025-03부터 Microsoft Defender가 `VulnerableDriver:WinNT/Winring0` /
  `HackTool:Win32/Winring0`로 차단·격리한다(취약 드라이버 블록리스트 등재). FanControl·OpenRGB·LHM 등이
  일제히 파손됐고, 이것이 LHM의 PawnIO 전환 동기다.
  출처: https://it.slashdot.org/story/25/03/14/1351225/windows-defender-now-flags-winring0-driver-as-security-threat-breaking-multiple-pc-monitoring-tools ·
  https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/issues/1660 ·
  https://www.pcworld.com/article/2912435/if-windows-defender-flags-winring0-on-your-gaming-pc-pay-attention.html
- "서비스로 상주 + 비관리자 프로세스가 디바이스 개방"이라는 구성 자체가 **CVE-2020-14979**(EVGA Precision X1,
  WinRing0 디바이스 NULL DACL)의 LPE 시나리오다 — 저권한 프로세스가 물리 메모리·MSR·IO 포트에 닿는다.
  LHM의 구 KernelDriver도 설치 직후 SDDL `O:BAG:SYD:(A;;FA;;;SY)(A;;FA;;;BA)`로 SYSTEM/관리자만 열게
  일부러 잠갔다(#1908 패치에서 삭제된 코드에서 확인).
  출처: https://medium.com/@matterpreter/cve-2020-14979-local-privilege-escalation-in-evga-precisionx1-cf63c6b95896 ·
  https://www.sentinelone.com/vulnerability-database/cve-2020-14979/

### 2.2 현대적 실현형 두 갈래

**①-a. PawnIO 설치(1회 관리자) + 디바이스 비관리자 개방**
- PawnIO는 서명 드라이버 + 서명 모듈만 실행하는 구조라 WinRing0보다 훨씬 안전하고 블록리스트 문제가 없다.
  2.x에 "디바이스를 비관리자에 개방" 옵션이 존재하나 **제작자가 비권장** — 서명 모듈이 제공하는 MSR 읽기
  등 프리미티브가 모든 저권한 프로세스에 노출된다(정보 유출·사이드채널·향후 취약점 시 전면 노출).
  안티치트(FACEIT)가 로드를 막은 사례도 있다(https://github.com/namazso/PawnIO.Setup/issues/1).
- 성립하면 LHM lib는 `\\.\PawnIO`를 그냥 CreateFile하므로 **KOTU 코드 수정 거의 0으로 비관리자 센서가
  나온다**(추정 — 개방 옵션의 정확한 설정 방법·지속성은 PawnIO.Setup 문서 실측 필요). 비용은 작지만
  "시스템 전역 보안 설정을 앱이 완화"하는 것이라 KOTU가 질 책임이 무겁다.

**①-b. 승격 수집 서비스 + IPC (상용 모니터링 앱 방식)**
- LocalSystem(또는 관리자) Windows 서비스가 LibreHardwareMonitorLib로 수집하고, 비관리자 UI가
  IPC로 받는다. 선례:
  - LibreHardwareService — LHM lib를 서비스로 상주, **공유 메모리**로 전 센서 노출(비관리자 소비):
    https://github.com/epinter/LibreHardwareService
  - HwMonitorService — LHM lib 서비스 래퍼, localhost **TCP**: https://github.com/Youda008/HwMonitorService
  - LHM 앱 자체도 WMI 네임스페이스 `root\LibreHardwareMonitor` + HTTP 서버로 외부 노출(단, 앱을 관리자로
    띄워 놓아야 함): https://ggfix.dk/blog/libre-hardware-monitor-capabilities-limits
- KOTU 구조 영향: `SensorService.Read()`에 "서비스 연결되면 IPC 프레임, 아니면 현행 in-proc" 폴백 층 추가.
  폴러 단일 스레드 구조(SensorService.cs:24-25)는 유지 가능. 서비스 exe는 별도 프로젝트 + 별도 수명.
- **Velopack 제약**: 서비스 설치 내장 지원 없음(요청 이슈 open: https://github.com/velopack/velopack/issues/305).
  기본 설치는 per-user·무승격이며(https://docs.velopack.io/packaging/installer), 훅(`--veloapp-install` 등,
  https://docs.velopack.io/integrating/hooks)도 사용자 권한으로 돈다. 현행 release.yml(109-134행 `vpk pack`)에
  끼울 자리가 없고, **앱 최초 실행 시 UAC 1회로 자체 등록**(sc create + PawnIO 설치)하는 형태가 된다.
  제거 시 `--veloapp-uninstall` 훅이 비승격이라 **서비스·드라이버가 잔존**할 수 있고(UAC를 또 띄워야 삭제),
  업데이트마다 서비스 exe 교체(서비스 중지→교체→시작) 시나리오도 앱이 직접 짜야 한다. MSI per-machine
  경로(`--msi`)도 있으나 현행 배포 체계(Setup.exe/Portable.zip, 자동 업데이트)와 어긋난다.
- 보안 리스크(명기): 승격 상주 프로세스 + IPC 표면이 새 공격면이다. IPC를 읽기 전용·무명령(수집 결과
  브로드캐스트만)으로 설계하고, 파이프/공유 메모리 ACL을 로그온 사용자로 제한해야 한다. 그래도
  "커널 접근 능력의 상주화"라는 본질 리스크는 남는다(백신 오탐 가능성 — A38과 같은 성격).

---

## 3. ②안 실현성 — 관리자 불필요 소스로 대체

- **현행 코드에 PerformanceCounter 사용은 0건**(저장소 grep — 문서 REQUIREMENTS.md:1477의 언급뿐).
  WMI는 스펙 표시(HardwareInfoService.cs — `Win32_Processor` 51행, `Win32_PhysicalMemory` 86행 등,
  관리자 불필요)에만 쓴다. 즉 ②는 신규 구현이다.
- 등재문 ②의 "CPU/메모리/네트워크/디스크는 PerfCounter·WMI로"는 채널 표와 어긋난다:
  **CpuLoad·RamLoad는 이미 비관리자 LHM에서 나오고**(§1.2), 네트워크·디스크 처리량은 애초 10채널에 없다
  (`SensorService.cs:162` "Network(A20에서 별도)"). 실제 회수 대상은 저하 5채널뿐이며 판정은:

| 잃는 채널 | 비관리자 회수 수단 | 판정 |
|---|---|---|
| CpuClock | PerfCounter `Processor Information\% Processor Performance` × 기본 클럭(WMI `Win32_Processor.MaxClockSpeed` — 이미 수집 중, HardwareInfoService.cs:51) | **가능**(근사치 — 유일하게 확실한 회수) |
| SsdTemp | `IOCTL_STORAGE_QUERY_PROPERTY` + `StorageDeviceTemperatureProperty`(Win10+, NVMe). MS 문서는 관리자 요구를 명시하지 않고, 비승격 작업관리자가 NVMe 온도를 표시하는 것이 방증. LHM lib 밖 자체 P/Invoke 필요 | **추정 가능**(NVMe 한정·실기기 검증 필수. SATA SMART는 불가) — 출처: https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ne-winioctl-storage_property_id · https://learn.microsoft.com/en-us/windows/win32/fileio/working-with-nvme-devices |
| CpuTemp | WMI `MSAcpi_ThermalZoneTemperature`(root\WMI) | **사실상 불가** — 관리자 요구 + 데스크톱 보드 대부분 미구현("Not supported")·구현돼도 CPU 코어가 아닌 보드/섀시 열영역. 출처: https://learn.microsoft.com/en-us/archive/msdn-technet-forums/a56ecf6f-e849-456e-994e-82d54830e1f7 · https://www.w3tutorials.net/blog/get-cpu-temperature-in-cmd-power-shell/ |
| CpuPower | MSR(RAPL) 전용 | **불가** |
| FanRpm | SuperIO/EC 직접 IO | **불가** |
- 결론: ② 단독의 화면 효과 = 10채널 중 **CpuClock 1개 확실 + SsdTemp 1개 조건부**. 핵심 요구(온도·팬)는
  못 채우므로 ②는 "저하 모드의 품질 개선"이지 A47의 완전 해법이 아니다. 다만 비용이 가장 작고 리스크가 0이다.

---

## 4. 권고안 — 3안 비교표와 택일 질문

### 4.1 비교표

| 안 | 효과(회복 채널) | 비용 | 리스크 | 판정 |
|---|---|---|---|---|
| ①-a PawnIO 설치 + 비관리자 개방 | 5/5 전부 | 小(설치 UI + 안내) | **高**: 제작자 비권장 구성, 시스템 전역 보안 완화, CVE-2020-14979 동형 | 비권장 |
| ①-b PawnIO 설치 + 승격 서비스 + IPC | 5/5 전부 | **大**: 서비스 프로젝트·IPC·UAC 온보딩·제거/업데이트 수명 관리(Velopack 무지원) | 中: 상주 승격 프로세스·백신 오탐·잔존물 | 완전 해법이나 후속 배치 규모 |
| ② 비관리자 소스 보강 | +CpuClock(확실)·+SsdTemp(NVMe 추정) — 온도·전력·팬은 저하 유지 | 小~中 | 無 | **선행 권고** |
| ③ requireAdministrator | 5/5 (항상 승격) | 최소 | 드래그&드롭·기본 앱 연동 파손, 전 기능 승격 | 비권장(등재문 확정 유지) |
| (공통 선결) 문서·주석 현행화 + PawnIO 미설치 감지 안내 | 오분류 해소(§1.4) | 極小 | 無 | 어느 안이든 필수 |

### 4.2 부록 B 등재용 택일 질문 문안 (1개)

> **[A47 확인 필요]** 조사 결과 LHM 0.9.6은 WinRing0가 제거되고 **별도 설치형 서명 드라이버 PawnIO** 기반이라,
> PawnIO가 없는 머신에서는 지금도 "Restart as admin" 후 CPU 온도·전력·클럭·팬이 계속 비고 안내도 안 뜹니다.
> 방향을 택일해 주세요:
> **(a)** 완전 해법 — 앱 최초 실행 시 UAC 1회로 PawnIO 설치 + 승격 수집 서비스 상주, UI는 비관리자로 IPC 수신
> (전 센서 회복, 구현·배포·제거 수명 비용 大, Velopack 미지원이라 자체 등록/해제 구현),
> **(b)** 저비용 보강 — PawnIO 미설치/비관리자 감지 안내 현행화 + 비관리자로 회수 가능한 것만 추가
> (CPU 클럭 근사·NVMe SSD 온도 시도, 온도·전력·팬은 종전대로 승격 시에만),
> **(c)** 현행 유지 + 문서·안내문만 정정.
> 권고 = **(b) 선행**, 이후 실사용 수요가 확인되면 (a)를 별도 항목으로.

### 4.3 등재문 정정 필요 사항 (어느 안이든)

- REQUIREMENTS.md:1475 "커널 드라이버(WinRing0)" → PawnIO로 정정(①의 전제 교체).
- KOTU.Module.Hardware.csproj:17-18·SensorService.cs:28·THIRD-PARTY-NOTICES.md:19의
  "커널 드라이버 동봉·자동 로드" 서술 정정.
- HANDOVER "실기기 확인 포인트" 후보: ㉮ 개발 머신에 PawnIO 설치 여부(`HKLM\...\Uninstall\PawnIO` 또는
  `C:\Program Files\PawnIO`), ㉯ PawnIO 없는 머신에서 승격 후 채널 실측(§1.4 ③ 검증),
  ㉰ 비관리자 NVMe 온도 IOCTL 실측(§3).
