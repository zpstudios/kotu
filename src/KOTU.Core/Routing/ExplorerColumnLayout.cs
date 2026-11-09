using KOTU.Core.Settings;

namespace KOTU.Core.Routing;

// 저장값과 기하는 불변이다. 표면은 같은 값을 읽고 명시적 사용자 동작만 저장한다.
public sealed record ExplorerColumnLayout(
    bool SizeVisible = true, bool TypeVisible = true, bool CreatedVisible = true, bool ModifiedVisible = true,
    bool CustomWidths = false, double NameWeight = 1, double SizeWeight = 32, double TypeWeight = 32,
    double CreatedWeight = 34, double ModifiedWeight = 34)
{
    public enum Column { Name, Size, Type, Created, Modified }
    public const string Prefix = "explorer.columns.v1.";
    public static ExplorerColumnLayout Default { get; } = new();
    public static IReadOnlyList<Column> Order { get; } = Array.AsReadOnly(Enum.GetValues<Column>());
    public bool IsVisible(Column column) => column switch
    {
        Column.Name => true, Column.Size => SizeVisible, Column.Type => TypeVisible,
        Column.Created => CreatedVisible, Column.Modified => ModifiedVisible, _ => false,
    };
    public double Weight(Column column) => column switch
    {
        Column.Name => NameWeight, Column.Size => SizeWeight, Column.Type => TypeWeight,
        Column.Created => CreatedWeight, Column.Modified => ModifiedWeight, _ => 1,
    };
    public static double Minimum(Column column) => column switch
    {
        Column.Name => 120, Column.Size or Column.Type => 48, _ => 96,
    };
    public ExplorerColumnLayout WithVisibility(Column column, bool visible) => column switch
    {
        Column.Size => this with { SizeVisible = visible }, Column.Type => this with { TypeVisible = visible },
        Column.Created => this with { CreatedVisible = visible }, Column.Modified => this with { ModifiedVisible = visible },
        _ => this,
    };
    private ExplorerColumnLayout WithWeight(Column column, double weight) => column switch
    {
        Column.Name => this with { NameWeight = weight }, Column.Size => this with { SizeWeight = weight },
        Column.Type => this with { TypeWeight = weight }, Column.Created => this with { CreatedWeight = weight },
        Column.Modified => this with { ModifiedWeight = weight }, _ => this,
    };
    public static string Key(Column column, string field) => Prefix + column.ToString().ToLowerInvariant() + "." + field;
    private static double SafeWeight(double value, double fallback) =>
        double.IsFinite(value) && value > 0 && value <= 1_000_000 ? value : fallback;
    public static ExplorerColumnLayout Read(ISettingsService settings)
    {
        var result = Default with { CustomWidths = settings.Get(Prefix + "custom", false) };
        foreach (var column in Order)
        {
            if (column != Column.Name) result = result.WithVisibility(column, settings.Get(Key(column, "visible"), true));
            result = result.WithWeight(column, SafeWeight(settings.Get(Key(column, "weight"), Default.Weight(column)), Default.Weight(column)));
        }
        return result;
    }

    // 폭은 행 전체 기준이다. 체크·아이콘·좌우 여백 68 DIP와 표시된 메타데이터의 간격만 뺀다.
    public double[] Measure(double rowWidth)
    {
        var result = new double[5];
        var visible = Order.Where(IsVisible).ToArray();
        var available = double.IsFinite(rowWidth) ? Math.Max(0, Math.Min(rowWidth, 1_000_000) - 68) : 0;
        var minimum = visible.Sum(Minimum);
        var gap = 8 * Math.Min(1, available / (minimum + 8 * (visible.Length - 1)));
        var content = Math.Max(0, available - gap * (visible.Length - 1));
        if (content < minimum)
        {
            foreach (var column in visible) result[(int)column] = content * Minimum(column) / minimum;
        }
        else if (!CustomWidths)
        {
            foreach (var column in visible.Where(column => column != Column.Name))
                result[(int)column] = column is Column.Size or Column.Type ? 80 : 130;
            var metadata = result.Sum();
            if (content - metadata >= Minimum(Column.Name)) result[0] = content - metadata;
            else
            {
                var extra = content - minimum;
                var defaultExtra = visible.Where(column => column != Column.Name)
                    .Sum(column => result[(int)column] - Minimum(column));
                foreach (var column in visible)
                    result[(int)column] = Minimum(column) + (column == Column.Name || defaultExtra == 0 ? 0 :
                        extra * (result[(int)column] - Minimum(column)) / defaultExtra);
                if (defaultExtra == 0) result[0] += extra;
            }
        }
        else
        {
            var weight = visible.Sum(column => SafeWeight(Weight(column), Default.Weight(column)));
            foreach (var column in visible)
                result[(int)column] = Minimum(column) + (content - minimum) * SafeWeight(Weight(column), Default.Weight(column)) / weight;
        }
        // 슬롯에는 왼쪽 간격을 포함하므로 숨긴 열은 간격까지 정확히 0이다.
        foreach (var column in visible.Where(column => column != Column.Name)) result[(int)column] += gap;
        return result;
    }

    public ExplorerColumnLayout Resize(double rowWidth, Column left, double delta)
    {
        if (!Enum.IsDefined(left) || !IsVisible(left) || !double.IsFinite(delta) || delta == 0) return this;
        var visible = Order.Where(IsVisible).ToArray();
        var index = Array.IndexOf(visible, left);
        if (index < 0 || index == visible.Length - 1) return this;
        if (!double.IsFinite(rowWidth) || rowWidth - 68 - 8 * (visible.Length - 1) <= visible.Sum(Minimum)) return this;
        var right = visible[index + 1];
        var widths = Measure(rowWidth);
        foreach (var column in visible.Where(column => column != Column.Name)) widths[(int)column] -= 8;
        var transfer = Math.Clamp(delta, Minimum(left) - widths[(int)left], widths[(int)right] - Minimum(right));
        if (Math.Abs(transfer) < 0.000001) return this;
        widths[(int)left] += transfer;
        widths[(int)right] -= transfer;
        var result = this with { CustomWidths = true };
        foreach (var column in visible)
            result = result.WithWeight(column, Math.Max(0.000001, widths[(int)column] - Minimum(column)));
        return result;
    }
}
