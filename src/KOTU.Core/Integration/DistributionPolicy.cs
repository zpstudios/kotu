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

    public static bool IsTestBuild
    {
        get
        {
#if KOTU_TEST_BUILD
            return true;
#else
            return false;
#endif
        }
    }

    // Stable owns the public update feed and the Windows Explorer registrations.
    public static bool IsStableDistribution => !IsStandalone && !IsTestBuild;
    public static string ProductId => IsTestBuild ? "KOTU.Test" : "KOTU";
    public static string InstanceKey => IsStandalone ? "KOTU-Standalone-Main" : ProductId + "-Main";
}
