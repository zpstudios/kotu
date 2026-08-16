using System.Globalization;
using System.Text.Json;

namespace KOTU.App.Integration;

/// <summary>
/// 관리자 재시작(A124) 창 세트 세션 파일 — %AppData%\KOTU\restart-session.json.
/// 명령줄 인자 길이 한계를 피하려고 파일로 전달한다.
///
/// 쓰는 쪽 = 하드웨어 모듈 "Restart as admin"의 runas 직전(WindowManager.WriteRestartSession —
/// Core의 RestartSession 훅 경유). 읽는 쪽 = 앱 시작(WindowManager.TryRestoreSession)이며,
/// 파일이 있으면 **읽는 즉시 삭제**한다(정리 책임 = 읽는 쪽). UTC 타임스탬프가 2분 이내인
/// 것만 유효 — 과거 잔재가 엉뚱한 시작을 재현하는 사고 방지. 무효 파일도 조용히 삭제·무시.
///
/// runas 승격은 같은 사용자 계정의 관리자 토큰이라 승격 프로세스의 %AppData%도 같은
/// 프로필이다 — 파일 교환이 성립한다. 승격 프로세스는 이 파일을 읽고 지울 뿐이며,
/// 설정 저장 경로(settings.json)는 여기서 건드리지 않는다.
///
/// 실패는 전부 조용히(TaskbarIdentity/A105 관례) — 세션 파일 문제가 재시작·시작을 막으면 안 된다.
/// </summary>
internal static class RestartSessionFile
{
    /// <summary>세션 파일 유효 기한 — 이보다 오래된(또는 미래의) 스냅샷은 잔재로 보고 버린다.</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(2);

    /// <summary>설정 파일과 같은 직렬화 방식(JsonSettingsService의 s_json 관례).</summary>
    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };

    /// <summary>
    /// 창 1개의 스냅샷 — A55 저장 항목(위치·크기·최대화, 물리 픽셀) 준용 + 모듈·파일.
    /// 휘발 상태(미저장 편집 내용·재생 위치·오버레이 상태)는 범위 밖(A124 확정).
    /// DTO 스타일은 SponsorAds.Entry 관례(속성 get/set + 리플렉션 직렬화).
    /// </summary>
    internal sealed class WindowSnapshot
    {
        public string? ModuleId { get; set; }

        /// <summary>열려 있던 파일. null이면 그 모듈의 빈 컨텍스트로 복원한다.</summary>
        public string? FilePath { get; set; }

        // 기하(물리 픽셀). Width/Height가 0이면 기하 캡처 실패 — 복원 창은 A55 승계 기하로 연다.
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public bool Maximized { get; set; }
    }

    /// <summary>파일 전체 — UTC 타임스탬프 + 창 목록(생성 순서 = 복원 순서).</summary>
    private sealed class Payload
    {
        /// <summary>ISO 8601 라운드트립("O") — UpdateCoordinator의 lastCheckedAt과 같은 표기.</summary>
        public string? SavedUtc { get; set; }

        public List<WindowSnapshot> Windows { get; set; } = [];
    }

    private static string PathOf() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Branding.AppName, "restart-session.json");

    /// <summary>
    /// 창 세트 스냅샷을 기록한다. 예외는 호출 계층(RestartSession.TryWrite)이 삼키므로
    /// 여기서는 방어하지 않는다 — 실패하면 파일이 없고, 승격 프로세스는 기본 1창으로 시작한다.
    /// </summary>
    public static void Write(List<WindowSnapshot> windows)
    {
        var path = PathOf();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new Payload
        {
            SavedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            Windows = windows,
        }, s_json));
    }

    /// <summary>기록한 세션 파일을 지운다 — UAC 취소(재시작 무산) 시 쓰는 쪽의 뒷정리.</summary>
    public static void Delete()
    {
        try
        {
            File.Delete(PathOf()); // 파일 부재는 no-op — File.Delete는 없는 파일에 던지지 않는다
        }
        catch
        {
            // 남아도 2분 기한이 무효화한다 — 조용히 무시.
        }
    }

    /// <summary>
    /// 세션 파일이 있으면 읽고 **즉시 삭제**한 뒤, 타임스탬프가 2분 이내(UTC)일 때만
    /// 창 목록을 돌려준다. 없음·파싱 실패·기한 초과·빈 목록은 전부 null —
    /// 호출자는 기본 1창 시작으로 후퇴한다(A124 조용한 폴백).
    /// </summary>
    public static IReadOnlyList<WindowSnapshot>? TryConsume()
    {
        try
        {
            var path = PathOf();
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path);
            Delete(); // 유효성 판정 전에 먼저 지운다 — 어떤 경로로든 잔재를 남기지 않는다

            Payload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<Payload>(text);
            }
            catch (JsonException)
            {
                return null; // 손상 파일 — 이미 지웠으니 조용히 무시
            }
            if (payload is null) return null;

            // 쓰는 쪽이 항상 UTC "O"를 기록한다 — UpdateCoordinator.ParseStamp와 같은 해석.
            if (!DateTimeOffset.TryParse(payload.SavedUtc, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var saved))
                return null;
            var age = DateTimeOffset.UtcNow - saved;
            if (age < TimeSpan.Zero || age > MaxAge) return null;

            // 손 편집으로 "Windows": null이 들어와도 조용히 무효 처리(역직렬화가 null을 넣을 수 있다).
            return payload.Windows is { Count: > 0 } list ? list : null;
        }
        catch
        {
            return null; // 읽기 실패가 시작을 막으면 안 된다
        }
    }
}
