using KOTU.Core.Appearance;
using KOTU.Core.Integration;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace KOTU.App.Appearance;

/// <summary>A gallery whose cards apply a design directly and mark the current choice.</summary>
internal sealed class DesignPicker : UserControl
{
    private readonly DesignPreferences _preferences;
    private readonly Grid _gallery = new() { ColumnSpacing = 12, RowSpacing = 12 };
    private readonly List<(Button Button, TextBlock Caption, string Id)> _cards = [];
    private int _columns;

    internal DesignPicker(DesignPreferences preferences)
    {
        _preferences = preferences;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        var root = new StackPanel { Spacing = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
        root.Children.Add(new TextBlock { Text = "Interface design", FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 0) });
        root.Children.Add(new TextBlock
        {
            Text = "Choose a design to apply it to all open windows.",
            TextWrapping = TextWrapping.Wrap, Opacity = .75,
        });
        if (DistributionPolicy.IsStandalone)
            root.Children.Add(new TextBlock { Text = "This design choice lasts for the current session.", TextWrapping = TextWrapping.Wrap, Opacity = .75 });
        BuildGallery();
        root.Children.Add(_gallery);
        Content = root;
        SizeChanged += (_, e) => ArrangeGallery(e.NewSize.Width);
        Loaded += (_, _) =>
        {
            DesignRuntime.Changed -= OnAppliedChanged;
            DesignRuntime.Changed += OnAppliedChanged;
            RefreshApplied(); ArrangeGallery(ActualWidth);
        };
        Unloaded += (_, _) => DesignRuntime.Changed -= OnAppliedChanged;
        RefreshApplied();
    }

    private void BuildGallery()
    {
        foreach (var design in DesignCatalog.All)
        {
            var body = new StackPanel { Spacing = 6 };
            body.Children.Add(new DesignPreviewRoot(design) { HorizontalAlignment = HorizontalAlignment.Stretch });
            body.Children.Add(new TextBlock { Text = design.Name, TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            var caption = new TextBlock { Text = " ", FontSize = 12, Opacity = .75 };
            body.Children.Add(caption);
            var card = new Button { Content = body, Padding = new Thickness(7),
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Top, BorderThickness = new Thickness(2) };
            DesignScope.SetRole(card, DesignElementRole.PreviewChoice);
            AutomationProperties.SetName(card, $"Apply {design.Name}");
            card.Click += (_, _) => { _preferences.Apply(design.Id); RefreshApplied(); };
            _cards.Add((card, caption, design.Id)); _gallery.Children.Add(card);
        }
        _columns = 0;
        ArrangeGallery(ActualWidth);
    }

    private void ArrangeGallery(double width)
    {
        // Eleven fixed specimens are bounded; no file enumeration or unbounded item generation.
        var columns = width >= 1000 ? 3 : width >= 600 ? 2 : 1;
        if (_columns == columns) return;
        _columns = columns; _gallery.ColumnDefinitions.Clear(); _gallery.RowDefinitions.Clear();
        for (var i = 0; i < columns; i++) _gallery.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < (DesignCatalog.All.Count + columns - 1) / columns; i++)
            _gallery.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var i = 0; i < _cards.Count; i++) { Grid.SetColumn(_cards[i].Button, i % columns); Grid.SetRow(_cards[i].Button, i / columns); }
    }

    private void OnAppliedChanged()
    {
        if (DispatcherQueue is { HasThreadAccess: false }) { DispatcherQueue.TryEnqueue(OnAppliedChanged); return; }
        if (!IsLoaded) return;
        RefreshApplied();
    }

    private void RefreshApplied()
    {
        var appliedId = _preferences.Current.Id;
        var accent = new Windows.UI.ViewManagement.UISettings()
            .UIElementColor(Windows.UI.ViewManagement.UIElementType.Highlight);
        foreach (var (button, caption, id) in _cards)
        {
            var applied = id == appliedId;
            caption.Text = applied ? "Applied" : " ";
            button.BorderBrush = applied ? new SolidColorBrush(accent)
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            AutomationProperties.SetHelpText(button, applied ? "Applied" : "Apply this design to all open windows.");
        }
    }
}
