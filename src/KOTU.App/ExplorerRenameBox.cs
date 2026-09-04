using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KOTU.App;

/// <summary>
/// A94 2차(v0.125.0): 인라인 이름변경 편집 상자 — F2·새 폴더 직후·우클릭 Rename의 공용 편집 수명 주기.
/// 항목 컨테이너 안의 이름 TextBlock을 잠시 Collapsed로 숨기고 같은 자리(같은 패널, 같은 Grid 행/열)에
/// 새 TextBox를 끼워 넣는다 — reparent가 아니라 새 요소 삽입이라 FrameworkElement.Parent 함정이 없고,
/// 끝나면 TextBox 제거 + TextBlock 복원이다. 이름 TextBlock을 찾는 일은 표면별 생성 코드가 제각각이라
/// 호출부(ExplorerPane·ThumbnailExplorer) 몫이고, 여기는 찾은 자리에서의 편집만 맡는다.
///
/// Enter/포커스 상실 = 커밋, Esc = 취소. 커밋 검증(빈 이름·잘못된 문자·충돌)은 ExplorerFileOps.Rename —
/// 실패하면 커밋하지 않고 안내 문구 + 원복이다(이동/복사와 달리 자동 "(2)"를 붙이지 않는다).
/// 파일이면 확장자 제외 부분만 선택(탐색기 관례), 폴더·확장자 없는 이름은 전체 선택.
///
/// 키가 새지 않는 근거: Enter·Esc는 여기서 Handled — 셸 루트 핸들러(OnShellEnter/OnShellEscape)는
/// Handled·텍스트 입력 포커스면 물러난다(MainWindow.OnRootKeyDown의 IsTextInputFocused 선행 판정).
/// 문자·Del·Ctrl 조합은 표면 KeyDown 핸들러(handledEventsToo 구독)가 e.OriginalSource(TextBox)
/// 가드로 걸러내고, A34 버튼 핫키·셸 단독 키·Shift+N은 HotkeySupport의 텍스트 입력 통과 규칙
/// (IsTextInput = TextBox 포함)이 이미 흘려보낸다.
///
/// 편집 도중 재스캔 금지: 갱신(onRenamed)은 커밋 성공 후에만 부른다. 같은 창의 다른 파일 조작은
/// 클릭·키 입력이 먼저 포커스를 옮겨 LostFocus 커밋이 선행되므로 편집 UI가 재스캔에 지워질 일이 없다.
///
/// A94 5차: 폴더 감시(ExplorerPane)의 자동 재스캔도 같은 금지를 지켜야 해서 활성 편집을 여기서
/// 정적으로 추적한다(IsEditing) — 감시는 편집 중 만료된 재스캔을 보류하고, 종료 알림(EditEnded)을
/// 받아 소화한다. 종료 알림은 커밋·취소·검증 실패 어느 길이든 상자가 걷힌 뒤 1회 나간다.
///
/// A345 배치 2(좌 리스트 UI 가상화): 편집 중 스크롤하면 그 행의 컨테이너가 <b>다른 파일 행으로
/// 재활용</b>된다 — 편집 상자가 그대로 남으면 엉뚱한 파일의 이름을 고치는 데이터 사고가 된다.
/// 그래서 컨테이너가 재활용 큐로 들어갈 때 호스트 패널을 넘겨 강제 커밋시킨다(ForceFinish).
/// 종료자(Finish)는 지역 함수라 밖에서 부를 수 없어, 상자의 Tag에 실어 두고 그것을 부른다
/// (상자 하나에 종료자 하나 — 이 Tag의 용도는 그 하나뿐이다).
/// </summary>
internal static class ExplorerRenameBox
{
    /// <summary>
    /// 지금 열려 있는 편집 상자들 (A94 5차). 보통 동시 1개(편집은 포커스 기반 — 같은 창의 새 편집·
    /// 클릭은 기존 편집의 LostFocus 커밋을 먼저 끝낸다)지만, 창이 여럿이면 창마다 1개씩 겹칠 수
    /// 있어 집합으로 센다. 접근은 단일 UI 스레드 전제(A110 — ExplorerFileOps.CutMarked와 동일).
    /// </summary>
    private static readonly HashSet<TextBox> ActiveBoxes = [];

    /// <summary>이름변경 편집이 진행 중인가 (A94 5차) — 폴더 감시(ExplorerPane)의 재스캔 보류 판정.</summary>
    internal static bool IsEditing => ActiveBoxes.Count > 0;

    /// <summary>
    /// 편집 1개가 끝났다 (A94 5차) — 커밋·취소·검증 실패 어느 길이든 상자가 걷힌 뒤 1회 발생.
    /// 폴더 감시(ExplorerPane)가 편집 중 보류한 재스캔을 소화하는 트리거. 다른 창의 편집이 아직
    /// 남았을 수 있으므로 수신 쪽은 IsEditing을 다시 확인한다. 정적 이벤트 — 구독은 표면 Loaded,
    /// 해지는 Unloaded(CutMarksChanged와 같은 수명 규칙).
    /// </summary>
    internal static event Action? EditEnded;

    /// <summary>
    /// A345 배치 2: 이 패널(항목 콘텐츠 루트)에 열려 있는 편집 상자를 <b>커밋</b>으로 끝낸다 —
    /// 호출부는 리스트 컨테이너가 재활용 큐로 들어가는 순간(ContainerContentChanging의
    /// InRecycleQueue)뿐이다. 커밋을 고른 이유: 사용자가 입력한 이름을 스크롤 한 번으로 버리는
    /// 것보다, 포커스를 잃었을 때와 같은 규칙(LostFocus = 커밋 — 탐색기 관례)이 일관된다.
    /// 열린 상자가 없으면 무동작이다(대부분의 재활용이 이 경우다 — Count 0 조기 반환).
    /// </summary>
    internal static void ForceFinish(Panel host)
    {
        if (ActiveBoxes.Count == 0) return;
        foreach (var box in ActiveBoxes.ToList()) // 종료가 집합을 줄이므로 복사본을 순회한다
        {
            if (!host.Children.Contains(box)) continue;
            if (box.Tag is Action<bool> finish) finish(true);
        }
    }

    /// <summary>추적 해제 + 종료 알림 — Finish(정상 종료)와 상자 Unloaded(창 닫힘 안전망) 양쪽에서 온다.</summary>
    private static void ClearActive(TextBox box)
    {
        if (!ActiveBoxes.Remove(box)) return; // 이미 한쪽이 처리했다 — 알림도 1회만
        EditEnded?.Invoke();
    }

    /// <summary>
    /// 편집 시작. host = 이름 TextBlock이 들어 있는 콘텐츠 패널(호출부가 생성 코드 구조로 찾은 것),
    /// path = 항목 전체 경로. ui = 그 표면의 UI 문맥(A94 4차 — 실패 보고 채널: 안내 문구 + 권한
    /// 부족이면 관리자 재시작 제안 대화상자. 종전 onNotice 콜백을 대체한다),
    /// onRenamed = 커밋 성공 후 재스캔.
    /// 이미 편집 중(TextBlock이 Collapsed)이면 중복 진입하지 않는다.
    /// </summary>
    internal static void Begin(Panel host, TextBlock nameBlock, string path,
        ExplorerFileOps.OpUi ui, Action onRenamed)
    {
        if (nameBlock.Visibility == Visibility.Collapsed) return; // 이미 편집 중 — 중복 진입 방지
        var originalName = Path.GetFileName(path);
        if (originalName.Length == 0) return; // 드라이브 루트 등 — 이름변경 대상이 아니다

        var box = new TextBox
        {
            Text = originalName,
            FontSize = nameBlock.FontSize,
            Margin = nameBlock.Margin,
            MinWidth = 64,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // Grid 부모(리스트 행·썸네일 타일)면 이름과 같은 칸에 앉힌다 — StackPanel(그리드 타일)에는
        // 무해한 기본값(0/0)이라 세 표면 공통으로 안전하다.
        Grid.SetRow(box, Grid.GetRow(nameBlock));
        Grid.SetColumn(box, Grid.GetColumn(nameBlock));
        host.Children.Insert(host.Children.IndexOf(nameBlock) + 1, box);
        nameBlock.Visibility = Visibility.Collapsed;
        // A94 5차: 활성 편집 추적 — 폴더 감시의 재스캔 보류 판정(IsEditing). 해제는 Finish 맨 끝.
        // Finish가 불리지 못한 채 트리에서 걷히는 경로(창 닫힘 등)는 상자 Unloaded가 안전망이다 —
        // 추적이 영구히 걸린 채 남으면 모든 창의 감시 재스캔이 멎기 때문. 정상 종료 뒤의 Unloaded는
        // ClearActive의 1회 가드(Remove 실패)로 무해하다.
        ActiveBoxes.Add(box);
        box.Unloaded += (_, _) => ClearActive(box);

        var done = false;
        void Finish(bool commit)
        {
            if (done) return; // Enter 커밋의 포커스 이동이 LostFocus를 재발화시킨다 — 1회만
            done = true;
            var typed = box.Text;
            host.Children.Remove(box);
            nameBlock.Visibility = Visibility.Visible; // 원복 — 성공해도 재스캔이 올 때까지는 옛 이름 표시
            if (commit && !string.Equals(typed.Trim(), originalName, StringComparison.Ordinal)) // 무변경 = 조용히 취소
            {
                var (error, denied) = ExplorerFileOps.Rename(path, typed);
                if (error is not null)
                    // 커밋하지 않음 — 원복 유지 (충돌·빈 이름·잘못된 문자·잠김 등). 발사 후 망각:
                    // 권한 부족이면 관리자 재시작 제안 대화상자가 뜨고, 그 대기가 편집 종료를 막지 않는다.
                    _ = ExplorerFileOps.ReportAsync(error, denied ? 1 : 0, ui);
                else
                    onRenamed();     // 성공 — 이제서야 재스캔(편집 중 재스캔 금지는 위 클래스 주석)
            }
            // A94 5차: 종료 알림은 결과(커밋·취소·실패)와 무관하게 맨 끝에 1회 — 감시가 보류한
            // 재스캔을 소화한다. 커밋 성공이면 onRenamed의 재스캔과 겹칠 수 있지만 그 재스캔이
            // 보류 플래그를 먼저 걷어 가고(ExplorerPane.TearDownWatch) _loadSeq도 있어 채우기는 1회다.
            ClearActive(box);
        }

        // A345 배치 2: 종료자를 상자에 실어 둔다 — 가상화 컨테이너 재활용 시 밖에서(ForceFinish)
        // 이 상자만 골라 커밋시키는 유일한 통로다(Finish는 지역 함수라 다른 접근 경로가 없다).
        box.Tag = (Action<bool>)Finish;

        box.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true; // 셸 Enter(A86/A90)·표면 Enter(항목 열기)로 새지 않게
                Finish(commit: true);
            }
            else if (e.Key == Windows.System.VirtualKey.Escape)
            {
                e.Handled = true; // S4의 셸 Esc(복귀)보다 편집 취소가 먼저 — OnShellEscape는 Handled면 물러난다
                Finish(commit: false);
            }
        };
        box.LostFocus += (_, _) => Finish(commit: true); // 딴 곳 클릭 = 커밋(탐색기 관례)

        box.Loaded += (_, _) =>
        {
            if (done) return;
            box.Focus(FocusState.Programmatic);
            var stem = Path.GetFileNameWithoutExtension(originalName);
            // 파일 = 확장자 제외 선택(탐색기 관례) / 폴더·확장자 없는 이름 = 전체 선택
            box.Select(0, Directory.Exists(path) || stem.Length == 0 ? originalName.Length : stem.Length);
        };
    }
}
