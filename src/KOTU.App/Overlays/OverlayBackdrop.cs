using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace KOTU.App.Overlays;

/// <summary>
/// 오버레이·S4 표면의 배경 브러시 선택 단일 지점 (A129, v0.156.0).
/// 반투명(오버레이 — A108 용어) 배경은 원래 인앱 아크릴(OverlayAcrylicBrush, A33) 하나였지만,
/// libvlc VideoView(비디오 화면·오디오 파형 시각화)는 XAML이 그리는 콘텐츠가 아니라 D3D
/// 스왑체인이 합성기에 별도 비주얼로 얹히는 구조라, 인앱 아크릴의 배경 샘플(같은 XAML 렌더
/// 결과)에 그 프레임이 들어가지 않는다. 아크릴 출력은 그 샘플의 불투명 합성이므로 비디오
/// 위에서 "불투명 판"으로 보인다(A129 실기기 보고 — 이미지 모듈은 XAML Image가 그리므로
/// 정상 반투명이던 이유. 오디오는 뒤가 검정 XAML 배경이라 티가 덜 났을 뿐 파형은 못 비춘다).
/// 그래서 스왑체인 위에서만 진짜 알파 합성이 되는 반투명 단색
/// (OverlayTranslucentFallbackBrush — App.xaml 다크/라이트 쌍, 알파는 아크릴 FallbackColor의
/// 6B와 동일)으로 바꿔 끼운다. 진짜 블러는 포기하고 투과만 성립한다(A129 통일 기준 = 반투명).
/// 선택 분기가 네 표면(FileListOverlay·ContentInfoOverlay·SidePanelHost·S4 ThumbnailExplorer)에
/// 흩어지지 않게 여기 한 곳으로 모았다 — 반투명 브러시 키 문자열도 이 파일과 App.xaml에만 둔다.
/// overSwapChain 신호의 출처는 셸 하나다: MainWindow.IsSwapChainContent를
/// ApplyOverlayStates(상태 변경 단일 종착점)가 매번 다시 밀어 주므로, 모듈 전환·파일 닫기 시
/// 아크릴 원복이 구조적으로 누락되지 않는다.
/// </summary>
internal static class OverlayBackdrop
{
    /// <summary>
    /// 표면 배경 브러시를 고른다. docked = 사이드바(불투명, A108 — 스왑체인 여부 무관) /
    /// overSwapChain = 지금 중앙 콘텐츠가 스왑체인 계열(비디오·오디오)이라는 셸 신호.
    /// 저장소 키 조회는 기존 SetState들과 같은 Application.Current.Resources 관용구 —
    /// ThemeDictionaries 키라 현재 테마의 값이 나온다(OverlayAcrylicBrush와 같은 해석 경로).
    /// </summary>
    public static Brush Pick(bool docked, bool overSwapChain) =>
        (Brush)Application.Current.Resources[
            docked ? "SolidBackgroundFillColorBaseBrush"
            : overSwapChain ? "OverlayTranslucentFallbackBrush"
            : "OverlayAcrylicBrush"];
}
