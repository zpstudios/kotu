using System.Runtime.InteropServices;

namespace KOTU.App.Integration;

/// <summary>
/// 윈도우 디스플레이 배율(OS 전체 배율) 조회·변경 (A48) — 창이 놓인 모니터 대상.
/// 모듈 프로젝트에는 DllImport를 두지 않는다는 규약에 따라 이 파일(셸)에만 있다.
/// DefaultAudioInput(A164)·DesktopWallpaper(A161)와 같은 격리 파일 패턴 — 전부 try/catch,
/// 실패 = 조용히 false(호출부가 ms-settings:display 딥링크로 폴백한다).
///
/// <b>비공식 API 사용</b>: 함수 자체(<c>DisplayConfigGetDeviceInfo</c>/<c>DisplayConfigSetDeviceInfo</c>)는
/// 문서화돼 있지만, DPI 배율을 다루는 type 값(GET = -3, SET = -4)과 그 구조체는 문서에 없다 —
/// Windows 설정 앱(immersive control panel)이 쓰는 경로를 리버싱한 커뮤니티 정의를 그대로 옮겼다.
/// 출처(원전): https://github.com/lihas/windows-DPI-scaling-sample (DPIHelper/DpiHelper.h) ·
/// https://github.com/imniko/SetDPI (DpiHelper.h). OS 업데이트로 깨질 수 있으며,
/// 그 경우 호출이 에러를 반환하거나 예외가 나므로 여기서 전부 false로 접힌다.
///
/// 값 인코딩: OS는 배율을 절대 %가 아니라 <b>권장(recommended) 배율로부터의 스텝 수</b>로
/// 주고받는다. 배율 후보 표는 <see cref="DpiPercents"/>(설정 앱에서 관찰된 값) — 최소는 항상
/// 100%이므로 minScaleRel(음수)로 권장 배율의 표 인덱스를 역산한다. 커스텀 %는 불가(프리셋만).
///
/// DPI 배율은 target(모니터)이 아니라 <b>source</b>의 속성이다(원전 명시) — 창의 HMONITOR에서
/// GDI 장치 이름(\\.\DISPLAYn)을 얻고, 활성 경로들의 source 이름(문서화된 type 1 =
/// DISPLAYCONFIG_DEVICE_INFO_TYPE_GET_SOURCE_NAME)과 대조해 adapterID+sourceID를 특정한다.
///
/// CI 실패 시 최소 복구 = 이 파일 삭제 + SettingsView의 Windows display scale 콤보 블록을
/// 딥링크 버튼(<see cref="TryOpenDisplaySettings"/> 동일 문자열 인라인)만으로 강등.
/// </summary>
internal static class DisplayScale
{
    /// <summary>
    /// OS가 쓰는 배율 후보 표(%) — 설정 앱에서 관찰·외삽된 값(원전 DpiVals 그대로).
    /// 상대 스텝 인덱스가 이 표를 가리킨다. UiScale.Percents(350까지)의 상위 호환.
    /// </summary>
    private static readonly int[] DpiPercents = [100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500];

    /// <summary>
    /// 창이 놓인 모니터의 현재·권장 배율(%)과 그 모니터가 지원하는 배율 목록을 조회한다.
    /// 실패(경로 특정 불가·API 에러·미래 OS에서 type -3 소멸) = false — 호출부는 콤보를
    /// 비활성화하고 딥링크만 남긴다.
    /// </summary>
    public static bool TryGet(IntPtr hwnd, out int currentPercent, out int recommendedPercent,
        out int[] availablePercents)
    {
        currentPercent = 0;
        recommendedPercent = 0;
        availablePercents = [];
        try
        {
            if (!TryFindSourceForWindow(hwnd, out var adapterId, out var sourceId)) return false;
            if (!TryGetScaleIndices(adapterId, sourceId, out var minIdx, out var curIdx,
                    out var recIdx, out var maxIdx))
                return false;
            currentPercent = DpiPercents[curIdx];
            recommendedPercent = DpiPercents[recIdx];
            availablePercents = DpiPercents[minIdx..(maxIdx + 1)];
            return true;
        }
        catch
        {
            return false; // 마샬링·OS 변형 등 전부 — 안내는 호출부(설정 화면)가 한다.
        }
    }

    /// <summary>
    /// 창이 놓인 모니터의 배율을 바꾼다. percent는 <see cref="DpiPercents"/>의 값이어야 하고
    /// (커스텀 % 불가 — OS 인코딩이 프리셋 인덱스다), 모니터 지원 범위 밖이면 범위로 클램프한다.
    /// 즉시 적용된다(설정 앱과 같은 경로 — 재로그인 불요). 실패 = false(호출부가 딥링크 폴백).
    /// </summary>
    public static bool TrySet(IntPtr hwnd, int percent)
    {
        try
        {
            var wantIdx = Array.IndexOf(DpiPercents, percent);
            if (wantIdx < 0) return false;
            if (!TryFindSourceForWindow(hwnd, out var adapterId, out var sourceId)) return false;
            // SET 전에 GET — 상대 스텝의 기준(권장 인덱스)과 클램프 범위가 여기서 나온다.
            if (!TryGetScaleIndices(adapterId, sourceId, out var minIdx, out _, out var recIdx,
                    out var maxIdx))
                return false;
            wantIdx = Math.Clamp(wantIdx, minIdx, maxIdx);

            var packet = new DpiScaleSet
            {
                header = new DeviceInfoHeader
                {
                    type = DisplayConfigDeviceInfoSetDpiScale,
                    size = (uint)Marshal.SizeOf<DpiScaleSet>(),
                    adapterId = adapterId,
                    id = sourceId,
                },
                scaleRel = wantIdx - recIdx,
            };
            return DisplayConfigSetDeviceInfo(ref packet) == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Windows 설정의 디스플레이 페이지를 연다 — SET/GET 실패 시의 폴백(부록 B 76 ③).
    /// ExplorerIntegration.OpenDefaultAppsSettings(A25)와 같은 딥링크 관용구.
    /// </summary>
    public static bool TryOpenDisplaySettings()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ms-settings:display",
                UseShellExecute = true,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 창의 HMONITOR → GDI 장치 이름(\\.\DISPLAYn) → 활성 경로의 source 이름 대조로
    /// 이 창이 놓인 모니터의 adapterID(LUID)+sourceID를 특정한다. 여기까지는 전부 문서화된
    /// API·구조체다(QueryDisplayConfig·type 1 GET_SOURCE_NAME).
    /// </summary>
    private static bool TryFindSourceForWindow(IntPtr hwnd, out Luid adapterId, out uint sourceId)
    {
        adapterId = default;
        sourceId = 0;

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return false;
        var monitorInfo = new MonitorInfoEx { cbSize = (uint)Marshal.SizeOf<MonitorInfoEx>() };
        if (!GetMonitorInfoW(monitor, ref monitorInfo)) return false;
        var gdiName = monitorInfo.szDevice;
        if (string.IsNullOrEmpty(gdiName)) return false;

        // 크기 조회와 본 조회 사이에 경로 수가 바뀌면(ERROR_INSUFFICIENT_BUFFER = 122) 다시 잰다.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out var numPaths, out var numModes) != 0)
                return false;
            var paths = new PathInfo[numPaths];
            var modes = new ModeInfo[numModes];
            var result = QueryDisplayConfig(QdcOnlyActivePaths, ref numPaths, paths,
                ref numModes, modes, IntPtr.Zero);
            if (result == ErrorInsufficientBuffer) continue;
            if (result != 0) return false;

            for (var i = 0; i < numPaths; i++)
            {
                var name = new SourceDeviceName
                {
                    header = new DeviceInfoHeader
                    {
                        type = DisplayConfigDeviceInfoGetSourceName,
                        size = (uint)Marshal.SizeOf<SourceDeviceName>(),
                        adapterId = paths[i].sourceInfo.adapterId,
                        id = paths[i].sourceInfo.id,
                    },
                };
                if (DisplayConfigGetDeviceInfo(ref name) != 0) continue;
                if (!string.Equals(name.viewGdiDeviceName, gdiName, StringComparison.OrdinalIgnoreCase))
                    continue;
                adapterId = paths[i].sourceInfo.adapterId;
                sourceId = paths[i].sourceInfo.id;
                return true;
            }
            return false; // 활성 경로에 이 모니터가 없음(원격 세션 등 특수 구성) — 폴백 경로로.
        }
        return false;
    }

    /// <summary>
    /// type -3(GET) 조회 결과를 <see cref="DpiPercents"/>의 절대 인덱스로 바꾼다.
    /// 원전 인코딩: 세 값 모두 권장 배율로부터의 상대 스텝이고 최소는 항상 100%(인덱스 0)라
    /// 권장 인덱스 = -minScaleRel. 표 범위를 벗어나면 구조체 해석이 어긋난 것이므로 실패 처리.
    /// </summary>
    private static bool TryGetScaleIndices(Luid adapterId, uint sourceId,
        out int minIdx, out int curIdx, out int recIdx, out int maxIdx)
    {
        minIdx = curIdx = recIdx = maxIdx = 0;
        var packet = new DpiScaleGet
        {
            header = new DeviceInfoHeader
            {
                type = DisplayConfigDeviceInfoGetDpiScale,
                size = (uint)Marshal.SizeOf<DpiScaleGet>(),
                adapterId = adapterId,
                id = sourceId,
            },
        };
        if (DisplayConfigGetDeviceInfo(ref packet) != 0) return false;
        recIdx = -packet.minScaleRel;
        minIdx = recIdx + packet.minScaleRel; // 항상 0이어야 하나 원전 수식 그대로 방어적으로 계산
        curIdx = recIdx + packet.curScaleRel;
        maxIdx = recIdx + packet.maxScaleRel;
        return minIdx >= 0 && minIdx <= curIdx && curIdx <= maxIdx && maxIdx < DpiPercents.Length
            && recIdx >= minIdx && recIdx <= maxIdx;
    }

    // ---- 상수 ----

    /// <summary>MONITOR_DEFAULTTONEAREST — 창이 걸쳐 있으면 가장 많이 겹치는 모니터.</summary>
    private const uint MonitorDefaultToNearest = 2;

    /// <summary>QDC_ONLY_ACTIVE_PATHS — 현재 켜져 있는 경로만.</summary>
    private const uint QdcOnlyActivePaths = 2;

    private const int ErrorInsufficientBuffer = 122;

    /// <summary>DISPLAYCONFIG_DEVICE_INFO_TYPE_GET_SOURCE_NAME(문서화) — source의 GDI 이름 조회.</summary>
    private const int DisplayConfigDeviceInfoGetSourceName = 1;

    /// <summary>비공식: DPI 배율 GET(min/cur/max 상대 스텝). 원전 enum 값 -3.</summary>
    private const int DisplayConfigDeviceInfoGetDpiScale = -3;

    /// <summary>비공식: DPI 배율 SET(권장 대비 상대 스텝 1개). 원전 enum 값 -4.</summary>
    private const int DisplayConfigDeviceInfoSetDpiScale = -4;

    // ---- 구조체 (필드 이름은 원전·SDK 헤더 표기 그대로 — 대조 검수용) ----

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    /// <summary>DISPLAYCONFIG_DEVICE_INFO_HEADER (wingdi.h, 문서화) — 20바이트.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoHeader
    {
        public int type;
        public uint size;
        public Luid adapterId;
        public uint id;
    }

    /// <summary>DISPLAYCONFIG_PATH_SOURCE_INFO (wingdi.h) — union 멤버가 전부 UINT32 1개라 평탄화.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PathSourceInfo
    {
        public Luid adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    /// <summary>DISPLAYCONFIG_PATH_TARGET_INFO (wingdi.h) — 48바이트, enum·BOOL은 전부 32비트.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PathTargetInfo
    {
        public Luid adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public uint refreshRateNumerator;
        public uint refreshRateDenominator;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    /// <summary>DISPLAYCONFIG_PATH_INFO (wingdi.h) — 72바이트.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PathInfo
    {
        public PathSourceInfo sourceInfo;
        public PathTargetInfo targetInfo;
        public uint flags;
    }

    /// <summary>
    /// DISPLAYCONFIG_MODE_INFO (wingdi.h) — 64바이트(헤더 16 + union 48, 최대 멤버 =
    /// DISPLAYCONFIG_TARGET_MODE의 VIDEO_SIGNAL_INFO). 내용은 안 쓰므로 union은 8바이트 6개로
    /// 자리만 잡는다(UINT64 pixelRate 때문에 정렬 8 유지 — 배열 원소 오프셋이 어긋나면 안 된다).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ModeInfo
    {
        public uint infoType;
        public uint id;
        public Luid adapterId;
        public ulong u0;
        public ulong u1;
        public ulong u2;
        public ulong u3;
        public ulong u4;
        public ulong u5;
    }

    /// <summary>DISPLAYCONFIG_SOURCE_DEVICE_NAME (wingdi.h, 문서화) — GDI 이름 32 WCHAR.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SourceDeviceName
    {
        public DeviceInfoHeader header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string viewGdiDeviceName;
    }

    /// <summary>
    /// 비공식(type -3 응답) DISPLAYCONFIG_SOURCE_DPI_SCALE_GET — 원전
    /// lihas/windows-DPI-scaling-sample DpiHelper.h 정의 그대로(int32 3개, 전부 권장 대비 스텝).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DpiScaleGet
    {
        public DeviceInfoHeader header;
        public int minScaleRel;
        public int curScaleRel;
        public int maxScaleRel;
    }

    /// <summary>
    /// 비공식(type -4 요청) DISPLAYCONFIG_SOURCE_DPI_SCALE_SET — 원전 정의 그대로(int32 1개).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DpiScaleSet
    {
        public DeviceInfoHeader header;
        public int scaleRel;
    }

    // ---- P/Invoke (전부 user32 — WindowMinSize·TrayIcon과 같은 DllImport 관용구) ----

    [StructLayout(LayoutKind.Sequential)]
    private struct RectPx
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    /// <summary>MONITORINFOEXW — szDevice(GDI 이름)를 얻으려고 EX 판을 쓴다.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public uint cbSize;
        public RectPx rcMonitor;
        public RectPx rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [DllImport("user32")]
    private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint dwFlags);

    [DllImport("user32", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MonitorInfoEx lpmi);

    [DllImport("user32")]
    private static extern int GetDisplayConfigBufferSizes(uint flags,
        out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32")]
    private static extern int QueryDisplayConfig(uint flags,
        ref uint numPathArrayElements, [Out] PathInfo[] pathArray,
        ref uint numModeInfoArrayElements, [Out] ModeInfo[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32")]
    private static extern int DisplayConfigGetDeviceInfo(ref SourceDeviceName requestPacket);

    [DllImport("user32")]
    private static extern int DisplayConfigGetDeviceInfo(ref DpiScaleGet requestPacket);

    [DllImport("user32")]
    private static extern int DisplayConfigSetDeviceInfo(ref DpiScaleSet requestPacket);
}
