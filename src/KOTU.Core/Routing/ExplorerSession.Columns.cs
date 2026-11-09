using KOTU.Core.Settings;

namespace KOTU.Core.Routing;

public sealed partial class ExplorerSession
{
    public ExplorerColumnLayout Columns { get; private set; } = ExplorerColumnLayout.Default;

    public ExplorerColumnLayout ReadColumns() => Columns = ExplorerColumnLayout.Read(PreferenceStore);

    public bool SetColumnVisible(string? folder, ExplorerColumnLayout.Column column, bool visible)
    {
        if (_disposed || !CanChangeFolderPreferences(folder) || !Enum.IsDefined(column) ||
            column == ExplorerColumnLayout.Column.Name) return false;
        var current = ReadColumns();
        if (current.IsVisible(column) == visible) return false;
        PreferenceStore.Set(ExplorerColumnLayout.Key(column, "visible"), visible);
        Columns = current.WithVisibility(column, visible);
        _settings?.RequestSave();
        return true;
    }

    public bool ResizeColumns(string? folder, ExplorerColumnLayout expected, double rowWidth,
        ExplorerColumnLayout.Column left, double delta)
    {
        if (_disposed || !CanChangeFolderPreferences(folder)) return false;
        var current = ReadColumns();
        // 다른 표면의 변경 또는 드래그 중 외부 변경 뒤에는 낡은 전체 너비를 저장하지 않는다.
        if (current != expected) return false;
        var next = current.Resize(rowWidth, left, delta);
        if (next == current) return false;
        foreach (var column in ExplorerColumnLayout.Order)
            if (current.Weight(column) != next.Weight(column))
                PreferenceStore.Set(ExplorerColumnLayout.Key(column, "weight"), next.Weight(column));
        if (!current.CustomWidths) PreferenceStore.Set(ExplorerColumnLayout.Prefix + "custom", true);
        Columns = next;
        _settings?.RequestSave();
        return true;
    }

    public bool ResetColumns(string? folder)
    {
        if (_disposed || !CanChangeFolderPreferences(folder)) return false;
        var current = ReadColumns();
        var defaults = ExplorerColumnLayout.Default;
        if (current == defaults) return false;
        foreach (var column in ExplorerColumnLayout.Order)
        {
            if (column != ExplorerColumnLayout.Column.Name && !current.IsVisible(column))
                PreferenceStore.Set(ExplorerColumnLayout.Key(column, "visible"), true);
            if (current.Weight(column) != defaults.Weight(column))
                PreferenceStore.Set(ExplorerColumnLayout.Key(column, "weight"), defaults.Weight(column));
        }
        if (current.CustomWidths) PreferenceStore.Set(ExplorerColumnLayout.Prefix + "custom", false);
        Columns = defaults;
        _settings?.RequestSave();
        return true;
    }
}
