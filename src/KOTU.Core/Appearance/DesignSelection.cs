namespace KOTU.Core.Appearance;

public enum DesignPickerMode { Single, Gallery }

/// <summary>A settings view owns this pending selection; browsing never mutates preferences.</summary>
public sealed class DesignSelection(string? appliedId)
{
    public DesignDefinition Applied { get; private set; } = DesignCatalog.Resolve(appliedId);
    public DesignDefinition Preview { get; private set; } = DesignCatalog.Resolve(appliedId);
    public DesignPickerMode Mode { get; set; }
    public bool HasPendingChange => Preview.Id != Applied.Id;
    public int PreviewIndex => DesignCatalog.IndexOf(Preview.Id);

    public void Select(string id) => Preview = DesignCatalog.Find(id)
        ?? throw new ArgumentException("Unknown interface design.", nameof(id));

    public void Move(int offset)
    {
        var count = DesignCatalog.All.Count;
        var index = ((long)PreviewIndex + offset) % count;
        Preview = DesignCatalog.All[(int)((index + count) % count)];
    }

    public void SynchronizeApplied(string? id)
    {
        var wasBrowsing = HasPendingChange;
        Applied = DesignCatalog.Resolve(id);
        if (!wasBrowsing) Preview = Applied;
    }

    public void Apply(DesignPreferences preferences)
    {
        preferences.Apply(Preview.Id);
        Applied = preferences.Current;
    }
}
