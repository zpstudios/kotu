using KOTU.Core.Appearance;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace KOTU.App.Appearance;

/// <summary>A bounded, native specimen using the same tokens as the live appearance service.</summary>
internal sealed class DesignPreviewRenderer
{
    private readonly DesignDefinition _d;
    private readonly DesignPalette _p;
    private readonly bool _highContrast;
    private const double Width = 900;
    private const double Height = 482;
    private DesignDecoration Kind => _d.Decoration;

    private DesignPreviewRenderer(DesignDefinition design, bool dark, bool highContrast)
    {
        _d = design;
        _highContrast = highContrast;
        _p = highContrast ? SystemPalette() : AppDesignService.Palette(design, dark);
    }

    internal static FrameworkElement Create(DesignDefinition design, bool dark, bool highContrast)
        => new DesignPreviewRenderer(design, dark, highContrast).Build();

    private FrameworkElement Build()
    {
        var shell = new Canvas { Width = Width, Height = Height, Background = Brush(_p.Background) };
        var inverse = Kind == DesignDecoration.Solid && !_highContrast;
        Add(shell, Surface(Width, 32, inverse ? _p.Foreground : _p.Panel, _p.Border, 0), 0, 0);
        Add(shell, Text("KOTU", 20, true, inverse ? _p.Background : _p.Foreground), 13, 3);
        Add(shell, Text("Documents", 12, color: inverse ? _p.Background : _p.Muted), 92, 9);
        var windowMarks = new[] { "—", "□", "×" };
        for (var i = 0; i < windowMarks.Length; i++)
        {
            var mark = Text(windowMarks[i], 16, color: inverse ? _p.Background : _p.Muted);
            mark.Width = 32;
            mark.TextAlignment = TextAlignment.Center;
            mark.TextWrapping = TextWrapping.NoWrap;
            Add(shell, mark, Width - 96 + i * 32, 4);
        }

        Add(shell, Command("up", 32), 12, 40);
        Add(shell, Command("home", 32), 50, 40);
        Add(shell, Text(Kind == DesignDecoration.Terminal ? "C:\\Users\\KOTU\\Documents >" : "Home  /  KOTU  /  Documents", 13), 94, 49);
        Add(shell, Command("grid", 32, selected: true), 680, 40);
        Add(shell, Command("list", 32), 718, 40);
        Add(shell, Command("sort", 84, label: "Name"), 794, 40);

        var floating = Kind is DesignDecoration.Rounded or DesignDecoration.Mechanical or DesignDecoration.Paper or DesignDecoration.Isometric;
        var gutter = floating ? 8d : 0d;
        var sideWidth = 177 - 2 * gutter;
        var sideFill = Kind == DesignDecoration.Paper ? _p.Card : Kind == DesignDecoration.Original ? _p.Background : _p.Panel;
        Add(shell, Surface(sideWidth, 300, sideFill,
            _p.Border, _d.PanelRadius, panel: true), gutter, 82);
        Add(shell, Surface(201 - 2 * gutter, 300, sideFill,
            _p.Border, _d.PanelRadius, panel: true), 699 + gutter, 82);
        Add(shell, Text("Locations", 15, true), 14 + gutter, 93);
        var rows = new[] { "Home", "Documents", "사진", "Downloads", "Music", "Videos" };
        for (var i = 0; i < rows.Length; i++)
        {
            var y = 122 + i * 32;
            if (i == 1) Add(shell, Surface(sideWidth - 16, 28, _p.Selection, _p.Accent, _d.ControlRadius), 8 + gutter, y);
            var rowInk = i == 1 ? (_highContrast ? _p.OnAccent : _p.Accent) : _p.Foreground;
            Add(shell, Icon(i == 0 ? "home" : "folder", 16, rowInk), 16 + gutter, y + 6);
            Add(shell, Text(rows[i], 13, color: rowInk), 40 + gutter, y + 4);
        }
        Add(shell, Text("C: 143 GB / 237 GB", 11, color: _p.Muted), 15 + gutter, 332);
        Add(shell, new Border { Width = sideWidth - 28, Height = 4, Background = Brush(_p.Border) }, 14 + gutter, 355);
        Add(shell, new Border { Width = (sideWidth - 28) * .6, Height = 4, Background = Brush(_p.Accent) }, 14 + gutter, 355);

        Add(shell, Text("6 items · 2 checked", 12, color: _p.Muted), 192, 93);
        Add(shell, Check(2), 648, 94);
        var files = new[] { ("Documents", "folder"), ("사진", "folder"), ("회의록.txt", "file"),
            ("demo.mp4", "play"), ("음악.flac", "music"), ("자료.pdf", "file") };
        // Editorial and terminal are explicitly typographic list compositions in the approved set.
        var list = Kind is DesignDecoration.Editorial or DesignDecoration.Terminal;
        for (var i = 0; i < files.Length; i++)
        {
            var selected = i == 2;
            var checkedItem = i is 2 or 4;
            var x = list ? 190 : 190 + i % 3 * 167;
            var y = list ? 122 + i * 39 : 122 + i / 3 * 116;
            var tileWidth = list ? 492d : 153d;
            var tileHeight = list ? 34d : 106d;
            var fill = selected ? _p.Selection : checkedItem ? _p.Panel : _p.Background;
            var fg = selected && _highContrast ? _p.OnAccent : _p.Foreground;
            if (Kind == DesignDecoration.Solid && selected) { fill = _p.Accent; fg = _p.OnAccent; }
            if (selected || checkedItem || Kind is DesignDecoration.Frame or DesignDecoration.Paper)
                Add(shell, Surface(tileWidth, tileHeight, fill, selected ? _p.Accent : _p.Border,
                    _d.ControlRadius, selected: selected), x, y);
            else if (list)
                Add(shell, new Border { Width = tileWidth, Height = 1, Background = Brush(_p.Border) }, x, y + tileHeight);
            Add(shell, Check(checkedItem ? 1 : 0), x + 6, y + 6);
            if (list)
            {
                Add(shell, Icon(files[i].Item2, 20, fg), x + 34, y + 7);
                Add(shell, Text(files[i].Item1, 14, Kind == DesignDecoration.Editorial, fg), x + 65, y + 6);
                Add(shell, Text(i < 2 ? "Folder" : "File", 11, color: _p.Muted), x + 414, y + 9);
            }
            else
            {
                Add(shell, Icon(files[i].Item2, 38, fg), x + 57, y + 24);
                var name = Text(files[i].Item1, 13, color: fg);
                name.Width = tileWidth - 12;
                name.TextAlignment = TextAlignment.Center;
                Add(shell, name, x + 6, y + 77);
            }
        }

        Add(shell, Icon("file", 20), 716 + gutter, 96);
        Add(shell, Text("Details", 16, true), 744 + gutter, 94);
        Add(shell, Text("회의록.txt", 15, true), 716 + gutter, 132);
        Add(shell, Text("Documents / 회의록.txt", 10, color: _p.Muted), 716 + gutter, 155);
        var properties = new[] { ("Type", "Text document"), ("Size", "4.2 KB"), ("Modified", "2026-11-21"), ("Encoding", "UTF-8") };
        for (var i = 0; i < properties.Length; i++)
        {
            Add(shell, Text(properties[i].Item1, 11, color: _p.Muted), 716 + gutter, 189 + i * 43);
            Add(shell, Text(properties[i].Item2, 13), 716 + gutter, 204 + i * 43);
        }

        // The specimen preserves the real application's 44-DIP bottom bar and 32-DIP controls.
        var barFill = inverse ? _p.Foreground : _p.Panel;
        Add(shell, Surface(Width, 44, barFill, _p.Border, Kind == DesignDecoration.Rounded ? 22 : 0), 0, 394);
        Add(shell, Command("menu", 32, inverse: inverse), 10, 400);
        Add(shell, Command("folder", 32, inverse: inverse), 48, 400);
        Add(shell, Command("file", 32, inverse: inverse), 86, 400);
        Add(shell, Text("2 checked · 1 selected", 12, color: inverse ? _p.Background : _p.Muted), 134, 408);
        Add(shell, Command("play", 32, inverse: inverse), 381, 400);
        Add(shell, Command("sound", 32, inverse: inverse), 419, 400);
        Add(shell, Command("filter", 190, label: "Filter names", inverse: inverse), 585, 400);
        Add(shell, Command("print", 32, disabled: true, inverse: inverse), 787, 400);
        Add(shell, Command("fit", 32, inverse: inverse), 825, 400);
        Add(shell, Command("expand", 32, inverse: inverse), 863, 400);

        Add(shell, Text("Aa 0123", 17, true, command:true), 14, 449);
        var states = new[] { (0, "Unchecked"), (1, "Checked"), (2, "Mixed"), (3, "Disabled") };
        for (var i = 0; i < states.Length; i++)
        {
            Add(shell, Check(states[i].Item1), 168 + i * 142, 452);
            Add(shell, Text(states[i].Item2, 12, color: _p.Muted), 193 + i * 142, 451);
        }
        Add(shell, Icon("settings", 19), 839, 450);
        Add(shell, Icon("sound", 19), 869, 450);
        return new Border { BorderBrush = Brush(_p.Border), BorderThickness = new Thickness(_d.BorderWidth),
            CornerRadius = new CornerRadius(_d.PanelRadius), Child = shell };
    }

    private FrameworkElement Command(string symbol, double width, bool selected = false, bool disabled = false,
        string? label = null, bool inverse = false)
    {
        var foreground = inverse ? _p.Background : selected ? _p.OnAccent : _p.Foreground;
        var fill = inverse ? _p.Foreground : selected ? _p.Accent : _p.Card;
        if (Kind is DesignDecoration.Editorial or DesignDecoration.Terminal)
        {
            fill = selected ? _p.Selection : _p.Background;
            foreground = selected ? (_highContrast ? _p.OnAccent : _p.Accent) : _p.Foreground;
        }
        var surface = Surface(width, 32, fill, selected ? _p.Accent : _p.Border, _d.ControlRadius, selected: selected);
        var grid = new Grid { Width = width, Height = 32, Opacity = disabled ? .35 : 1 };
        grid.Children.Add(surface);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(Icon(symbol, 18, foreground));
        if (label is not null) row.Children.Add(Text(label, 12, color: foreground, command:true));
        grid.Children.Add(row);
        return grid;
    }

    private Grid Surface(double width, double height, uint fill, uint line, double radius, bool selected = false, bool panel = false)
    {
        var grid = new Grid { Width = width, Height = height };
        var surface = new DesignSurface();
        surface.Draw(_d, _p with { Border=line }, width, height, _highContrast, false, panel, selected, fill, radius);
        grid.Children.Add(surface);
        return grid;
    }
    private FrameworkElement Check(int state)
    {
        if (Kind == DesignDecoration.Terminal)
            return Text(state == 1 ? "[x]" : state == 2 ? "[-]" : "[ ]", 13, color: state == 3 ? _p.Muted : _p.Foreground);
        var selected = state is 1 or 2;
        var grid = new Grid { Width = 17, Height = 17, Opacity = state == 3 ? .35 : 1 };
        var original = Kind == DesignDecoration.Original && !_highContrast;
        grid.Children.Add(Surface(17, 17, selected ? _p.Accent : original ? _p.Background : _p.Card,
            selected ? _p.Accent : original ? _p.Muted : _p.Border, _d.CheckRadius));
        if (selected)
        {
            var check = KOTU.Ui.DesignIcons.Create(state == 1 ? "Check" : "Mixed", _d, 13);
            check.Foreground = Brush(_p.OnAccent);
            grid.Children.Add(check);
        }
        return grid;
    }

    private FrameworkElement Icon(string name, double size, uint? color = null)
    {
        var symbol = name switch
        {
            "up" => "Parent", "grid" => "Preview", "sound" => "Speaker", "sort" => "Up",
            "expand" => "Fullscreen", "file" => "Document", _ => name,
        };
        var icon = KOTU.Ui.DesignIcons.Create(symbol, _d, size);
        icon.Foreground = Brush(color ?? _p.Foreground);
        return icon;
    }
    private TextBlock Text(string value, double size, bool heading = false, uint? color = null, bool command = false) => new()
    {
        Text = value, FontFamily = new FontFamily(Kind == DesignDecoration.Original && !command ? "Segoe UI" : heading ? _d.HeadingFontFamily : _d.FontFamily),
        FontSize = Kind == DesignDecoration.Ink ? size * 1.18 : size,
        FontWeight = new Windows.UI.Text.FontWeight { Weight = heading ? (ushort)600 : _d.FontWeight },
        Foreground = Brush(color ?? _p.Foreground), TextTrimming = TextTrimming.CharacterEllipsis,
        MaxLines = 1,
    };

    private static SolidColorBrush Brush(uint argb) => new(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
    private static void Add(Canvas parent, UIElement child, double x, double y)
    {
        Canvas.SetLeft(child, x); Canvas.SetTop(child, y); parent.Children.Add(child);
    }

    private static DesignPalette SystemPalette()
    {
        var settings = new UISettings();
        uint C(UIElementType type) { var c = settings.UIElementColor(type); return ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B; }
        return new(C(UIElementType.Window), C(UIElementType.Window), C(UIElementType.ButtonFace), C(UIElementType.WindowText),
            C(UIElementType.GrayText), C(UIElementType.WindowText), C(UIElementType.Highlight), C(UIElementType.Highlight), C(UIElementType.HighlightText));
    }
}
