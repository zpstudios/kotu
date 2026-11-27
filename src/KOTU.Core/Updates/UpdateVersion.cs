using System.Globalization;

namespace KOTU.Core.Updates;

/// <summary>공식 안정판의 세 자리 버전. AssemblyVersion의 네 번째 자리는 배포 버전이 아니다.</summary>
public readonly record struct UpdateVersion(int Major, int Minor, int Patch) : IComparable<UpdateVersion>
{
    public static bool TryParse(string? text, out UpdateVersion version)
    {
        version = default;
        if (text is null) return false;
        var parts = text.Split('.');
        if (parts.Length != 3) return false;
        var values = new int[3];
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length == 0 || part.Any(c => c is < '0' or > '9') ||
                (part.Length > 1 && part[0] == '0') ||
                !int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out values[i])) return false;
        }
        version = new(values[0], values[1], values[2]);
        return true;
    }

    public static UpdateVersion FromInstalled(Version version)
        => new(version.Major, version.Minor, Math.Max(0, version.Build));

    public bool ShouldPromptOver(UpdateVersion installed) => CompareTo(installed) > 0 && Minor > installed.Minor;

    public int CompareTo(UpdateVersion other)
    {
        var result = Major.CompareTo(other.Major);
        if (result == 0) result = Minor.CompareTo(other.Minor);
        return result == 0 ? Patch.CompareTo(other.Patch) : result;
    }

    public override string ToString() => FormattableString.Invariant($"{Major}.{Minor}.{Patch}");
}
