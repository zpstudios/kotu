using System.Runtime.ExceptionServices;
using System.Text;

namespace KOTU.Core.Diagnostics;

/// <summary>
/// A352 배치 1: 줄 단위 즉시 flush 트레이스 로그 — 설정의 숨김 토글(diag.trace, 기본 꺼짐)로만 켠다.
/// <para>
/// 왜 계측판(NavDiagnostics)이 아니라 파일인가: All Readable 대형 폴더에서 프로세스가
/// <b>통째로 사라진다</b>(이벤트 1000 · 오류 모듈 Microsoft.UI.Xaml.dll · 예외 코드 0xC000027B =
/// STATUS_STOWED_EXCEPTION). 그 갈래는 XAML 비동기 콜백·바인딩에서 난 예외가
/// <c>Application.UnhandledException</c>까지 오지 못하고 런타임이 프로세스를 끝내는 것이라
/// 기존 안전망(Program.LogFatal → startup-error.log)에 한 줄도 남지 않는다. 화면 스트립도
/// 죽는 순간과 함께 사라지므로, 남는 유일한 증거는 <b>이미 디스크에 내려간 줄</b>이다.
/// 그래서 이 시설은 조립·버퍼링을 하지 않고 매 줄 파일에 밀어 넣는다 — 마지막 줄이 곧 범인의 위치다.
/// </para>
/// <para>
/// <b>Core에 두는 이유</b>: 모듈 프로젝트(영상·오디오·이미지·문서·All Readable)는 KOTU.App을
/// 참조하지 못한다(ARCHITECTURE 3장). 기록 지점이 셸과 모듈 양쪽에 있어야 하므로 기록 시설 자체는
/// Core에 있고, <b>설정 키·변경 알림은 App 쪽 얇은 래퍼</b>(TraceDiagnostics)가 맡는다 —
/// 이 클래스는 설정을 모른다(NavDiagnostics/ShellDiagnostics의 토글 관용구를 그대로 나눠 가진 형태).
/// </para>
/// <para>
/// <b>비용 계약</b>: 꺼짐이면 <see cref="Write"/>가 bool 하나 읽고 즉시 반환한다(파일 핸들도 없다).
/// 켜져 있으면 UI 스레드에서도 매 줄 동기 flush라 느리다 — 이것은 의도된 대가다(진단 전용·기본
/// 꺼짐이고, 버퍼에 남은 줄은 크래시 때 통째로 사라져 쓸모가 없다). 호출부는 문자열 보간 비용이
/// 아까운 뜨거운 경로(ContainerContentChanging·미리보기 fetch)에서만 <see cref="Enabled"/>를 먼저 읽는다.
/// </para>
/// <para>
/// <b>어떤 예외도 밖으로 내지 않는다</b>: 진단이 진단 대상을 죽이면 안 된다. 모든 진입점이
/// try/catch로 통째로 감싸여 있고, 실패하면 그 줄을 조용히 버린다.
/// </para>
/// </summary>
public static class DiagTrace
{
    /// <summary>로그 폴더 이름 — %TEMP% 아래 앱 폴더다(Program.LogPath의 startup-error.log와 같은 자리).
    /// Branding.AppName은 KOTU.App에 있어 Core에서 참조할 수 없으므로 리터럴을 쓴다 —
    /// JsonSettingsService가 설정 폴더에 같은 리터럴을 쓰는 선례가 이미 있다(리네이밍 시 함께 손댈 곳).</summary>
    private const string BrandFolder = "KOTU";

    /// <summary>회전 임계치(바이트) — 넘으면 trace.1.log로 밀고 새 파일을 연다. 보관은 1개뿐이다
    /// (사용자가 재현 직후 파일을 전달하는 용도라 더 오래된 이력은 필요 없다).</summary>
    private const long MaxBytes = 1024 * 1024;

    private static readonly object Gate = new();

    /// <summary>열려 있는 로그 — null이면 꺼져 있다(SetEnabled가 여는 유일한 지점).</summary>
    private static StreamWriter? _writer;

    /// <summary>지금까지 이 파일에 쓴 바이트 수(회전 판정용). 이어쓰기로 열면 기존 길이에서 시작한다.</summary>
    private static long _written;

    /// <summary>훅(FirstChanceException 등)을 실제로 걸어 두었는가 — 켬/끔이 겹쳐도 한 벌만 유지한다.</summary>
    private static bool _hooked;

    /// <summary>기록 중 재진입 방어. FirstChanceException 핸들러 안에서 예외가 나면 그 예외가
    /// 다시 핸들러를 부르므로 스택이 무한히 감긴다 — 스레드별 깃발로 두 번째 진입을 즉시 접는다.</summary>
    [ThreadStatic] private static bool _inWrite;

    /// <summary>진단 켜짐 — 모든 진입점의 앞단 게이트. volatile: 켜고 끄는 스레드(설정 화면이 있는 창)와
    /// 읽는 스레드(다른 창의 UI 스레드·워커)가 다르다.</summary>
    private static volatile bool _enabled;

    public static bool Enabled => _enabled;

    /// <summary>로그 파일 경로 — 설정 화면의 "Open log folder" 버튼과 안내 문구가 함께 쓴다.</summary>
    public static string LogPath =>
        Path.Combine(Path.GetTempPath(), BrandFolder, "trace.log");

    /// <summary>회전본 경로 — 현행 파일이 상한을 넘으면 이 이름으로 밀린다(보관 1개).</summary>
    private static string RolledPath =>
        Path.Combine(Path.GetTempPath(), BrandFolder, "trace.1.log");

    /// <summary>
    /// 토글 반영(App의 MainWindow.ApplyTraceDiagnostics 전용). 프로세스 전역 상태라 창이 여럿이어도
    /// 같은 값으로 여러 번 불릴 수 있다 — <b>같은 값이면 무동작</b>이라 파일이 다시 열리거나 훅이
    /// 두 번 걸리지 않는다. 켜면 파일을 이어쓰기로 열고 헤더 한 줄(버전·시각)을 남긴 뒤 훅을 걸고,
    /// 끄면 훅을 떼고 파일을 닫는다.
    /// </summary>
    public static void SetEnabled(bool on)
    {
        lock (Gate)
        {
            if (on == _enabled) return;
            if (on)
            {
                if (!TryOpenLocked()) return; // 파일을 못 열면 켜지 않는다(꺼진 상태 유지 = 비용 0)
                _enabled = true;
                Hook();
                // 헤더도 재진입 깃발을 세우고 쓴다 — 이 줄을 쓰는 도중 예외가 나면
                // 방금 건 FirstChanceException 훅이 같은 스레드에서 다시 들어오는데,
                // lock은 재진입 가능이라 깃발이 없으면 그대로 파고든다.
                _inWrite = true;
                try { WriteLocked("trace", Header()); }
                catch { /* 헤더 실패는 삼킨다 — 기록 자체는 계속한다 */ }
                finally { _inWrite = false; }
            }
            else
            {
                _enabled = false;
                Unhook();
                CloseLocked();
            }
        }
    }

    /// <summary>
    /// 한 줄 기록 — 형식은 <c>[HH:mm:ss.fff] [T{관리 스레드 id}] category | message</c>다.
    /// 스레드 id를 넣는 이유: stowed 예외는 UI 스레드가 아닌 곳에서 난 예외가 XAML 콜백을 타고
    /// 올라오는 갈래가 많아, 마지막 줄들이 같은 스레드인지 아닌지가 판독의 절반이다.
    /// </summary>
    public static void Write(string category, string message)
    {
        if (!_enabled) return;
        if (_inWrite) return; // 재진입(기록 중에 난 예외의 first-chance) — 조용히 접는다
        _inWrite = true;
        try
        {
            lock (Gate) WriteLocked(category, message);
        }
        catch
        {
            // 진단이 대상을 죽이지 않는다 — 디스크 가득·권한·경합은 그 줄을 버리는 것으로 끝낸다.
        }
        finally
        {
            _inWrite = false;
        }
    }

    private static void WriteLocked(string category, string message)
    {
        if (_writer is not { } writer) return;
        // 시각 서식에는 대괄호를 넣지 않는다 — 사용자 지정 날짜 서식에서 리터럴 문자를 섞으면
        // 읽기 어려워지므로 순수 서식만 만들고 괄호는 아래 연결에서 붙인다.
        var line = string.Concat(
            "[", DateTime.Now.ToString("HH:mm:ss.fff"), "] ",
            "[T", Environment.CurrentManagedThreadId.ToString(), "] ",
            category, " | ", message);
        writer.WriteLine(line);
        writer.Flush(); // 매 줄 — 크래시가 버퍼를 통째로 삼키지 않게(이 시설의 존재 이유다)
        _written += line.Length + 2; // 근사치면 충분하다(회전 판정용 — 정확한 바이트 수가 아니어도 된다)
        if (_written > MaxBytes) RollLocked();
    }

    /// <summary>파일을 이어쓰기로 연다(호출부가 Gate를 잡고 있다). 실패하면 false — 켜지지 않는다.</summary>
    private static bool TryOpenLocked()
    {
        try
        {
            var path = LogPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // FileShare.ReadWrite: 사용자가 로그를 켠 채로 메모장 등으로 열어 봐도 기록이 막히지 않게.
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            _writer = new StreamWriter(stream, new UTF8Encoding(false));
            _written = stream.Length;
            return true;
        }
        catch
        {
            CloseLocked();
            return false;
        }
    }

    private static void CloseLocked()
    {
        try { _writer?.Dispose(); }
        catch { /* 닫기 실패는 삼킨다 */ }
        _writer = null;
        _written = 0;
    }

    /// <summary>상한을 넘은 파일을 trace.1.log로 밀고 새 파일을 연다(보관 1개).
    /// 밀기에 실패하면(회전본이 잠겨 있는 등) 같은 파일에 이어 쓰되 카운터는 0으로 되돌린다 —
    /// 실패할 때마다 곧바로 다시 회전을 시도해 재귀로 감기는 것을 막는 장치다. 새 파일을 아예
    /// 못 열면 <c>_writer</c>가 null로 남아 이후 줄이 조용히 버려진다(꺼진 것과 같은 상태).</summary>
    private static void RollLocked()
    {
        try
        {
            CloseLocked();
            var rolled = RolledPath;
            if (File.Exists(rolled)) File.Delete(rolled);
            File.Move(LogPath, rolled);
        }
        catch
        {
            // 이동 실패(잠김 등) — 아래 재개방이 이어쓰기라 파일이 조금 더 커질 뿐이다.
        }
        if (!TryOpenLocked()) return;
        _written = 0;
        WriteLocked("trace", "rolled " + Header());
    }

    /// <summary>헤더 한 줄 — 어느 버전에서 언제 켠 로그인지. 어셈블리 버전은 SettingsView의
    /// About 줄과 같은 조회 방식이다(Assembly.GetName().Version).</summary>
    private static string Header()
    {
        var version = typeof(DiagTrace).Assembly.GetName().Version?.ToString(3) ?? "?";
        return $"{BrandFolder} v{version} · {DateTime.Now:yyyy-MM-dd HH:mm:ss} · pid {Environment.ProcessId}";
    }

    // ---------- 전역 예외 훅 ----------
    // 셋 다 켤 때 1회 걸고 끌 때 뗀다(_hooked 가드). 목표는 하나 — 프로세스가 사라지기 직전의
    // 마지막 예외를 디스크에 남기는 것이다. stowed 예외(0xC000027B)에서는 마지막 first-chance가
    // 사실상 범인이므로 그 갈래가 이 셋 중 제일 중요하다.

    private static void Hook()
    {
        if (_hooked) return;
        _hooked = true;
        AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
        TaskScheduler.UnobservedTaskException += OnUnobserved;
    }

    private static void Unhook()
    {
        if (!_hooked) return;
        _hooked = false;
        AppDomain.CurrentDomain.FirstChanceException -= OnFirstChance;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandled;
        TaskScheduler.UnobservedTaskException -= OnUnobserved;
    }

    /// <summary>던져진 모든 예외(잡히기 전) — 이 저장소는 정상 동작에서도 예외를 삼키는 곳이
    /// 많으므로(썸네일 실패·잠긴 파일 등) 줄 수가 많다. 그래서 스택은 <b>첫 프레임 한 줄</b>만
    /// 적는다(전체 스택은 비용도 크고 파일도 금방 회전시킨다).</summary>
    private static void OnFirstChance(object? sender, FirstChanceExceptionEventArgs e)
    {
        if (!_enabled || _inWrite) return;
        try
        {
            var ex = e.Exception;
            Write("exc", $"{ex.GetType().Name}: {ex.Message} @ {FirstFrame(ex)}");
        }
        catch
        {
            // 핸들러에서 새면 프로세스가 죽는다 — 무조건 삼킨다.
        }
    }

    private static void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        if (!_enabled) return;
        try
        {
            var ex = e.Exception;
            Write("unobserved", $"{ex.GetType().Name}: {ex.Message} @ {FirstFrame(ex)}");
        }
        catch
        {
            // 위와 동일 — 삼킨다(Observed 표시는 하지 않는다: 진단이 앱의 예외 정책을 바꾸면 안 된다).
        }
    }

    /// <summary>처리되지 않은 예외 — Program.LogFatal과 <b>별개</b>다(둘 다 남는다).
    /// 이쪽은 시각·스레드가 붙은 채 trace.log의 마지막 줄로 들어가므로 직전 줄들과 이어서 읽힌다.</summary>
    private static void OnUnhandled(object? sender, UnhandledExceptionEventArgs e)
    {
        if (!_enabled) return;
        try
        {
            var text = e.ExceptionObject is Exception ex
                ? $"{ex.GetType().Name}: {ex.Message} @ {FirstFrame(ex)}"
                : e.ExceptionObject?.ToString() ?? "(null)";
            Write("fatal", $"terminating={e.IsTerminating} {text}");
        }
        catch
        {
            // 위와 동일 — 삼킨다.
        }
    }

    /// <summary>스택의 첫 줄(가장 안쪽 프레임) — 없으면 빈 자리 표시. 문자열 전체를 다루지 않고
    /// 첫 개행까지만 잘라 비용을 묶는다.</summary>
    private static string FirstFrame(Exception ex)
    {
        var stack = ex.StackTrace;
        if (string.IsNullOrEmpty(stack)) return "(no stack)";
        var end = stack.IndexOf('\n');
        return (end < 0 ? stack : stack[..end]).Trim();
    }
}
