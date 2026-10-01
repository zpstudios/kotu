namespace KOTU.Core.Integration;

/// <summary>Distribution identity is compiled into Core, never inferred from paths or environment.</summary>
public static class DistributionPolicy
{
    public static bool IsStandalone
    {
        get
        {
#if KOTU_STANDALONE
            return true;
#else
            return false;
#endif
        }
    }

    public static string InstanceKey => IsStandalone ? "KOTU-Standalone-Main" : "KOTU-Main";
}
