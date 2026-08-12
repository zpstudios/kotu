using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using KOTU.Core.Settings;

namespace KOTU.App.Overlays;

/// <summary>
/// 파일 리스트 오버레이 공용 컨트롤 (A57 ②) — 기존 MainWindow의 AltOverlayRoot(우측 30%,
/// v0.25.0)를 추출해 좌측 30%로 스왑(A57 ①)한 것. 내부는 ExplorerPane 리스트 전용 모드 재사용.
/// 컨텍스트는 모듈이 주입한다: Show(folder, extensions)의 확장자 목록이 모듈별 필터(A57 ③)가 되고,
/// ExplorerPane의 A7 드롭다운은 그 안에서 추가로 좁힌다. 적용 대상은 파일 모듈
/// (Image·Video·Audio·Document·Archive) — 정보(H/W)·설정 모듈은 셸이 파일 경로가 없어
/// 애초에 Show를 부르지 않는다(현행 동작 유지).
/// 입력(Alt 홀드·2연타 고정, A32)은 셸(MainWindow)이 그대로 담당한다 — A58에서 별도 대체 예정.
/// </summary>
public sealed partial class FileListOverlay : UserControl
{
    private ExplorerPane? _list; // 지연 생성 (기존 MainWindow._altList와 동일 수명)

    /// <summary>파일 더블클릭 열기 — 셸이 재사용 규칙(A24)을 적용해 라우팅한다.</summary>
    public event Action<string>? FileActivated;

    /// <summary>명시적 새 창 열기(A24: Shift+더블클릭·우클릭 메뉴) — 셸이 항상 새 창으로.</summary>
    public event Action<string>? FileActivatedNewWindow;

    /// <summary>정렬 키 저장용(A5) — 셸이 리스트 첫 생성 전에 주입한다. 없어도 동작(기본 이름순).</summary>
    public ISettingsService? Settings { get; set; }

    /// <summary>오버레이가 화면에 떠 있는지 — 셸의 표시 갱신·Alt 키 소비 판단에 쓴다.</summary>
    public bool IsOpen => Visibility == Visibility.Visible;

    public FileListOverlay()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 모듈 컨텍스트를 주입받아 표시한다: folder = 현재 파일의 폴더,
    /// extensions = 모듈 담당 확장자(IModule.SupportedExtensions — A57 ③ 모듈별 필터).
    /// 이미 떠 있으면 폴더·필터만 갱신한다(모듈 전환 시 ExplorerPane이 A7 필터를 재구성).
    /// </summary>
    public void Show(string folder, IReadOnlyList<string> extensions)
    {
        if (_list is null)
        {
            _list = new ExplorerPane { Settings = Settings }; // 정렬 키 저장(A5)
            _list.ConfigureListOnly();
            _list.FileActivated += path => FileActivated?.Invoke(path);
            _list.FileActivatedNewWindow += path => FileActivatedNewWindow?.Invoke(path);
            ListHost.Content = _list;
        }
        _list.NavigateTo(folder, extensions);
        Visibility = Visibility.Visible;
    }

    public void Hide() => Visibility = Visibility.Collapsed;

    /// <summary>고정(2연타) 안내 문구 표시 — 떠 있는 상태에서 고정됐을 때만 보인다(v0.32.0).</summary>
    public void SetPinned(bool pinned) =>
        PinnedText.Visibility = IsOpen && pinned ? Visibility.Visible : Visibility.Collapsed;
}
