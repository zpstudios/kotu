namespace KOTU.Core.Content;

/// <summary>창 표시와 패널 구성을 독립적으로 관리한다. S4 임시 표면은 이 상태를 바꾸지 않는다.</summary>
public sealed class ShellLayoutPolicy
{
    public bool IsFullScreen { get; private set; }
    public bool ListVisible { get; private set; } = true;
    public bool InfoVisible { get; private set; } = true;
    public bool IsOverride { get; private set; }
    public bool IsPanelless => !ListVisible && !InfoVisible;
    public bool EffectiveListVisible => ListVisible && (!IsFullScreen || IsOverride);
    public bool EffectiveInfoVisible => InfoVisible && (!IsFullScreen || IsOverride);
    public string? OpenedPath { get; private set; }
    public bool HasContent { get; private set; }

    public void Reset(string? path = null, bool untitled = false)
    {
        OpenedPath = path;
        HasContent = path is not null || untitled;
        ListVisible = InfoVisible = !HasContent;
        IsOverride = false;
        IsFullScreen = false;
    }

    /// <summary>같은 콘텐츠의 중복 완료 통지는 사용자가 바꾼 구성을 되돌리지 않는다.</summary>
    public bool ObserveOpened(string? path, bool untitled = false)
    {
        if (HasContent && string.Equals(OpenedPath, path, StringComparison.OrdinalIgnoreCase)) return false;
        Reset(path, untitled);
        return true;
    }

    /// <summary>저장은 콘텐츠 교체가 아니므로 창과 패널 상태를 보존한다.</summary>
    public void SavedPath(string path)
    {
        OpenedPath = path;
        HasContent = true;
    }

    public void SetFullScreen(bool value) => IsFullScreen = value;

    public void ToggleSidebar(bool list)
    {
        ListVisible = EffectiveListVisible;
        InfoVisible = EffectiveInfoVisible;
        if (list) ListVisible = !ListVisible;
        else InfoVisible = !InfoVisible;
        IsOverride = !IsPanelless;
    }
}
