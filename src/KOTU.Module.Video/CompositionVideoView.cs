using LibVLCSharp.Platforms.Windows;

namespace KOTU.Module.Video;

// A412: 라이브러리의 공개 템플릿 수명만 알린다. 표면 생성·파괴는 원래 컨트롤이 소유한다.
public sealed class CompositionVideoView : VideoView
{
    internal event EventHandler? TemplateApplied;

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        TemplateApplied?.Invoke(this, EventArgs.Empty);
    }
}
