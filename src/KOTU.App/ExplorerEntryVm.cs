using System.ComponentModel;
using System.Runtime.CompilerServices;
using KOTU.Core.Routing;

namespace KOTU.App;

/// <summary>
/// A345 배치 1: 탐색기 두 표면(좌 리스트 ExplorerPane · 중앙 타일 ThumbnailExplorer)의
/// 항목 하나를 대표하는 데이터 축(뷰모델). 스캔 결과인 <see cref="ExplorerListing.Entry"/>는
/// 불변 record라 "표시 상태"(상세 줄·툴팁·체크·잘라내기 흐림)를 담을 자리가 없어,
/// 그 위에 얇게 씌운 표시 전용 래퍼다 — 선례는 ArchiveView의 ArchiveRow이고, 이 클래스는
/// 거기에 변경 통지(INotifyPropertyChanged)를 더한 확장형이다.
/// <para>
/// 이 배치의 범위: 컨테이너의 Tag가 Entry 대신 이 뷰모델을 담는다(항목 조회·선택·클릭·드래그·
/// 체크 판정이 전부 그 Tag 패턴이라 축을 한 번에 옮긴다). 화면은 종전과 완전히 같다 —
/// 아직 아무도 이 속성들을 바인딩하지 않고, 컨테이너 값은 종전대로 직접 대입한다.
/// 값이 두 벌(컨테이너 · 뷰모델)이 되는 지점은 셋뿐이고(ApplyDetail · 체크 토글 ·
/// ApplyCutMark) 전부 <b>한 함수 안에서 둘 다</b> 갱신해 어긋날 여지를 없앴다.
/// </para>
/// <para>
/// 배치 2(좌 리스트 가상화)가 그것을 실행했다: 좌 리스트(ListPane)는 이제 이 뷰모델 목록을
/// ItemsSource로 받고 DataTemplate의 x:Bind가 아래 속성들을 읽는다 — 리스트 쪽 컨테이너 직접
/// 대입은 사라졌다(그리드 IconGrid는 휴면 표면이라 종전 컨테이너 조립·Tag = 뷰모델 그대로다).
/// 변경 통지가 필요한 이유 = 가상화 뒤에는 화면 밖 항목의 값도 보존돼야 하기 때문
/// (재활용된 컨테이너가 옛 값을 들고 오는 사고의 방지선 — 재활용 시 x:Bind OneWay가 새 항목
/// 값으로 다시 평가되는 것이 그 방지선의 실체다).
/// </para>
/// <para>
/// 접근성이 public인 이유(배치 2): DataTemplate의 x:Bind는 컴파일 시 이 타입을 참조하는 코드를
/// 생성한다 — 선례 ArchiveView의 ArchiveRow도 같은 이유로 public sealed다. 생성자와
/// <see cref="Entry"/>는 internal 그대로라 외부 어셈블리가 만들거나 원본을 꺼낼 수는 없다.
/// </para>
/// <para>
/// 배치 3(중앙 타일 가상화)이 두 번째 소비자다 — 중앙 썸네일(ThumbnailExplorer)의 TileGrid도
/// 이제 이 뷰모델 목록을 ItemsSource로 받는다(캡션·툴팁·잘라내기 흐림·클라우드 배지 =
/// DataTemplate의 x:Bind). 중앙 표면이 더 얹은 것 = <b>미리보기 캐시 필드 4종</b>이다:
/// 타일 미리보기(텍스트 앞부분 읽기·셸 썸네일 추출·오디오 셸 속성)는 건당 파일 IO라,
/// 스크롤로 컨테이너가 재활용된 뒤 그 항목이 다시 실체화될 때마다 같은 IO를 반복하면
/// 왕복 스크롤이 곧 디스크 폭주가 된다. 그래서 <b>결과를 뷰모델에 남기고</b>(PreviewText·
/// AudioInfo), 얻을 것이 없다고 확정된 항목은 다시 요청하지 않는다(PreviewKnownEmpty).
/// 비트맵만은 캐시하지 않는다 — 768px 디코드 × 스크롤한 항목 수는 메모리 폭주이고,
/// 셸 썸네일 캐시가 이미 있어 재추출이 싸다는 것이 A242의 근거 그대로다.
/// </para>
/// </summary>
public sealed class ExplorerEntryVm : INotifyPropertyChanged
{
    internal ExplorerEntryVm(ExplorerListing.Entry entry) => Entry = entry;

    /// <summary>원본 스캔 결과 — 공개 API(SelectedEntry·ViewChanged·FillCompleted)가 돌려주는 값.</summary>
    internal ExplorerListing.Entry Entry { get; }

    // ---------- Entry 위임 getter ----------
    // Tag 패턴 매칭이 중첩 속성(Tag: ExplorerEntryVm { IsFolder: false })을 쓰므로 그대로 노출한다.

    public string Path => Entry.Path;

    public string Name => Entry.Name;

    public bool IsFolder => Entry.IsFolder;

    /// <summary>A175 — 클라우드 전용(하이드레이션 유발) 파일인지. 상세·썸네일 취득이 이 값으로 갈린다.</summary>
    public bool IsPlaceholder => Entry.IsPlaceholder;

    /// <summary>
    /// 행 아이콘 글리프 (배치 2) — 폴더/문서. 종전 MakeListItem·MakeGridItem이 코드에서 고르던
    /// 규칙 그대로이고, 항목마다 불변이라 통지가 필요 없다(x:Bind 기본 OneTime으로 읽는다).
    /// </summary>
    public string Glyph => IsFolder ? "\uE8B7" : "\uE7C3";

    /// <summary>
    /// A337 클라우드 배지의 표시 여부 (A345 배치 3) — 중앙 타일 DataTemplate이 x:Bind로 읽는다.
    /// 값이 항목마다 불변이라 통지가 없다(x:Bind 기본 OneTime). 종전에는 타일 조립 코드가
    /// 같은 조건으로 FontIcon을 만들지 말지 갈랐다(MakeCloudBadge 호출 여부) — 가상화 뒤에는
    /// 배지가 템플릿에 항상 있고 이 속성이 보일지를 정한다.
    /// </summary>
    public Microsoft.UI.Xaml.Visibility CloudBadgeVisibility =>
        !IsFolder && IsPlaceholder
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>
    /// 배치 2: 상세 조각 fetch를 이미 한 번 요청했는지 — 컨테이너가 재활용될 때마다
    /// ContainerContentChanging이 같은 항목을 다시 태우므로, 중복 fetch를 이 표지로 막는다.
    /// 표시값이 아니라 내부 표지라 통지하지 않는다(UI 스레드 단독 접근).
    /// </summary>
    internal bool DetailRequested { get; set; }

    // ---------- 중앙 타일 미리보기 캐시 (A345 배치 3) ----------
    // 넷 다 표시값이 아니라 내부 표지·캐시라 통지하지 않는다(UI 스레드 단독 접근 — 미리보기
    // 발사도 완료 반영도 전부 UI 스레드다). 화면 갱신은 캐시를 읽는 쪽(ContainerContentChanging
    // 위상 0)이 직접 한다.

    /// <summary>A233 텍스트 프리뷰의 결과 문자열 — 재실체화 시 파일을 다시 읽지 않고 즉시 그린다.</summary>
    internal string? PreviewText { get; set; }

    /// <summary>A270 오디오 정보 2줄 — 셸 속성 왕복 1회의 결과. 재실체화 시 재조회하지 않는다.</summary>
    internal string? AudioInfo { get; set; }

    /// <summary>
    /// 미리보기로 얻을 것이 없다고 확정됐는지 (셸 썸네일 없음·파일 종류 아이콘·읽기 실패).
    /// 확정된 항목은 재실체화돼도 다시 요청하지 않는다 — 없는 것을 스크롤할 때마다 다시
    /// 물어보면 왕복 스크롤이 그대로 IO 폭주가 된다.
    /// </summary>
    internal bool PreviewKnownEmpty { get; set; }

    /// <summary>
    /// 지금 이 항목의 미리보기를 비동기로 요청해 둔 상태인지 — 같은 뷰모델이 위상 1을 두 번
    /// 태우는 경우(스크롤 왕복으로 짧은 사이에 다시 실체화)에 같은 IO가 겹치지 않게 막는다.
    /// </summary>
    internal bool PreviewInFlight { get; set; }

    // ---------- 표시 상태(통지 있음) ----------

    private string _detailText = string.Empty;

    /// <summary>리스트 행 2줄째 상세 텍스트 (A156 BuildDetailText 결과) — 지연 로드로 다시 채워진다.</summary>
    public string DetailText
    {
        get => _detailText;
        set => Set(ref _detailText, value);
    }

    private string _tooltipText = string.Empty;

    /// <summary>리스트 행 툴팁 (A156 BuildTooltipText 결과) — DetailText와 같은 시점에 갱신된다.</summary>
    public string TooltipText
    {
        get => _tooltipText;
        set => Set(ref _tooltipText, value);
    }

    private bool _isChecked;

    /// <summary>작업 집합 체크 (A179) — 진실은 여전히 경로 키 집합(_checkedPaths)이고 이건 그 시각화다.</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set => Set(ref _isChecked, value);
    }

    private double _contentOpacity = 1.0;

    /// <summary>잘라내기 흐림 (A94 4차) — 컨테이너가 아니라 '콘텐츠'의 투명도라는 규칙 그대로.</summary>
    public double ContentOpacity
    {
        get => _contentOpacity;
        set => Set(ref _contentOpacity, value);
    }

    private bool _isCurrent;

    /// <summary>
    /// A351: 지금 셸이 열어 놓은 콘텐츠 파일인지 — <b>선택과는 다른 축</b>이다.
    /// A323은 이 표시를 선택(SelectedItem)으로 대신했는데, 그러면 "사용자가 고른 행"과
    /// "열려 있는 파일"이 같은 어두운 배경 하나로 겹쳐 구분이 되지 않았다(사용자 보고).
    /// 이제 전용 시각 요소 둘이 이 값에 달려 있다 — 좌측 액센트 바
    /// (<see cref="CurrentMarkVisibility"/>)와 이름 굵기(<see cref="NameWeight"/>).
    /// <para>
    /// 세터를 <c>Set</c> 헬퍼로 쓰지 않는 이유: 그 헬퍼는 자기 이름 하나만 통지하는데
    /// 여기는 파생 둘까지 세 이름을 함께 통지해야 한다(안 하면 재활용된 컨테이너의 x:Bind가
    /// 옛 굵기·옛 바를 들고 온다).
    /// </para>
    /// </summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value) return;
            _isCurrent = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrent)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentMarkVisibility)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NameWeight)));
        }
    }

    /// <summary>
    /// A351: 좌측 액센트 바를 보일지 — 리스트 DataTemplate이 x:Bind OneWay로 읽는다
    /// (선례 = 중앙 타일의 <see cref="CloudBadgeVisibility"/>. 그쪽은 불변이라 OneTime이다).
    /// </summary>
    public Microsoft.UI.Xaml.Visibility CurrentMarkVisibility =>
        _isCurrent
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>
    /// A351: 열린 파일의 이름만 굵게 — SemiBold 선례는 ThumbnailExplorer.MakeExtensionTile.
    /// </summary>
    public Microsoft.UI.Text.FontWeight NameWeight =>
        _isCurrent
            ? Microsoft.UI.Text.FontWeights.SemiBold
            : Microsoft.UI.Text.FontWeights.Normal;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>값이 실제로 바뀔 때만 통지 — 같은 값 재대입이 재렌더를 부르지 않게 한다.</summary>
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
