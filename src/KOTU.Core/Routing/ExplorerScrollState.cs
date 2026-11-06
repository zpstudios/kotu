namespace KOTU.Core.Routing;

/// <summary>Logical location survives a different surface's physical end-of-list clamp.</summary>
public sealed class ExplorerScrollState
{
    public readonly record struct Anchor(string? Path, bool Parent);
    public Anchor Logical { get; private set; } = new(null, true);
    public long Epoch { get; private set; }
    public bool UserNavigation { get; private set; }
    public bool ProgrammaticRestore { get; private set; }
    private (Anchor Anchor, double Offset)? _physicalSample;

    public long BeginRestore(Anchor anchor)
    {
        InvalidatePhysicalSample();
        Logical = anchor;
        UserNavigation = false;
        ProgrammaticRestore = true;
        return ++Epoch;
    }

    public void CompleteRestore(long epoch)
    {
        if (epoch == Epoch) ProgrammaticRestore = false;
        // A late native ViewChanged still has no permission to replace the logical location.
    }

    public long BeginUserNavigation()
    {
        InvalidatePhysicalSample();
        UserNavigation = true;
        ProgrammaticRestore = false;
        return ++Epoch;
    }

    public bool ObservePhysical(Anchor anchor)
    {
        if (!UserNavigation || ProgrammaticRestore) return false;
        if (anchor.Path is null && !anchor.Parent) return false;
        Logical = anchor;
        return true;
    }

    public void InvalidatePhysicalSample() => _physicalSample = null;

    /// <summary>Publish only a repeated valid viewport sample from the current input epoch.</summary>
    public bool ObserveSettledPhysical(Anchor anchor, double offset, long epoch)
    {
        if (epoch != Epoch || !UserNavigation || ProgrammaticRestore) return false;
        if ((anchor.Path is null && !anchor.Parent) || !double.IsFinite(offset))
        {
            InvalidatePhysicalSample();
            return false;
        }
        var sample = (anchor, offset);
        var settled = _physicalSample == sample;
        _physicalSample = sample;
        return settled && ObservePhysical(anchor);
    }

    /// <summary>Reveal only the clipped edge; never move an already visible focused row.</summary>
    public static double VisibleOffset(double current, double maximum, double viewportHeight,
        double rowTop, double rowHeight)
    {
        if (viewportHeight <= 0 || rowHeight <= 0) return current;
        var delta = rowTop < 0 || rowHeight > viewportHeight ? rowTop
            : Math.Max(0, rowTop + rowHeight - viewportHeight);
        return Math.Clamp(current + delta, 0, Math.Max(0, maximum));
    }
}
