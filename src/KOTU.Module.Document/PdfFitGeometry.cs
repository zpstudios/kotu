namespace KOTU.Module.Document;

/// <summary>
/// PDF 맞춤 보기 모드(A49 — A30 규격 준용). Contain = 페이지가 뷰포트보다 크면 전부 보이게
/// 줄이고, 작으면 100%(원본 크기) = <b>축소만</b> — A83이 3모듈 공통으로 확정한 의미론이다
/// (구 이름 AutoFit에서 계산식·동작 변화 없음. zoom = min(1:1 배율, 가로 맞춤, 세로 맞춤)).
/// </summary>
public enum PdfFitMode { Contain, FitWidth, FitHeight, ActualSize }

/// <summary>PDF 맞춤 산식. 모든 입력은 같은 뷰포트 좌표의 DIP이며 줌 전 페이지 크기를 받는다.</summary>
internal static class PdfFitGeometry
{
    public static double VisibleHeight(double width, double height,
        double overlayX, double overlayY, double overlayWidth, double overlayHeight)
    {
        // 하단 오버레이와 실제로 겹칠 때만 그 위까지를 가시 높이로 쓴다.
        if (overlayWidth <= 0 || overlayHeight <= 0 || overlayX >= width
            || overlayX + overlayWidth <= 0 || overlayY >= height
            || overlayY + overlayHeight < height - 0.5) return height;
        return Math.Clamp(overlayY, 0, height);
    }

    public static double Zoom(double width, double visibleHeight, double pageWidth,
        double pageHeight, double nativeWidth, PdfFitMode mode, double minZoom, double maxZoom)
    {
        var fitWidth = width / (pageWidth + 2);
        var fitHeight = visibleHeight / (pageHeight + 18);
        var actual = nativeWidth > 0 ? nativeWidth / pageWidth : 1.0;
        var zoom = mode switch
        {
            PdfFitMode.FitWidth => fitWidth,
            PdfFitMode.FitHeight => fitHeight,
            PdfFitMode.ActualSize => actual,
            _ => Math.Min(actual, Math.Min(fitWidth, fitHeight)),
        };
        return Math.Clamp(zoom, minZoom, maxZoom);
    }
}
