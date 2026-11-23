using KOTU.Core.Appearance;
using KOTU.Core.Integration;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace KOTU.App.Appearance;

/// <summary>One pending selection shared by carousel and gallery; Apply is the only writer.</summary>
internal sealed class DesignPicker : UserControl
{
    private readonly DesignPreferences _preferences;
    private readonly DesignSelection _selection;
    private readonly Grid _gallery = new() { ColumnSpacing = 12, RowSpacing = 12 };
    private readonly Border _preview = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _title = new() { FontSize = 20, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _description = new() { Opacity = .75, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _applied = new() { Opacity = .75, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _position = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 52, TextAlignment = TextAlignment.Center };
    private readonly Button _apply = new() { Content = "Apply design", MinWidth = 120 };
    private readonly Button _applyBelow = new() { Content = "Apply previewed design", HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Button _showApplied = new() { Content = "Show applied design" };
    private readonly Button _openSingle = new() { Content = "Open single view", Visibility = Visibility.Collapsed };
    private readonly RadioButton _single = new() { Content = "Single view", IsChecked = true };
    private readonly List<(Button Button, TextBlock Caption, string Id)> _cards = [];
    private int _columns;

    internal DesignPicker(DesignPreferences preferences)
    {
        _preferences = preferences;
        _selection = new DesignSelection(preferences.Current.Id);
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        var root = new StackPanel { Spacing = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
        root.Children.Add(new TextBlock { Text = "Interface design", FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 0) });
        root.Children.Add(new TextBlock
        {
            Text = "Compare all eleven designs. Browsing changes the preview; Apply design changes all open windows.",
            TextWrapping = TextWrapping.Wrap, Opacity = .75,
        });
        if (DistributionPolicy.IsStandalone)
            root.Children.Add(new TextBlock { Text = "This design choice lasts for the current session.", TextWrapping = TextWrapping.Wrap, Opacity = .75 });
        var group = "KotuDesignView_" + Guid.NewGuid().ToString("N");
        _single.GroupName = group;
        var multiple = new RadioButton { Content = "Gallery view", GroupName = group };
        var modes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 20 };
        modes.Children.Add(_single); modes.Children.Add(multiple); root.Children.Add(modes);
        _single.Checked += (_, _) => SetMode(DesignPickerMode.Single);
        multiple.Checked += (_, _) => SetMode(DesignPickerMode.Gallery);
        root.Children.Add(_applied);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(_apply); actions.Children.Add(_showApplied); root.Children.Add(actions);
        root.Children.Add(_openSingle);
        _openSingle.Click += (_, _) =>
        {
            _single.IsChecked = true;
            _preview.StartBringIntoView();
        };
        _apply.Click += (_, _) => Apply();
        _applyBelow.Click += (_, _) => Apply();
        _showApplied.Click += (_, _) => { _selection.Select(_preferences.Current.Id); RefreshSelection(); };
        _gallery.Visibility = Visibility.Collapsed;
        root.Children.Add(_gallery);
        var navigation = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var previous = new Button { Content = "Previous", MinWidth = 90 };
        var next = new Button { Content = "Next", MinWidth = 90 };
        AutomationProperties.SetName(previous, "Preview previous interface design");
        AutomationProperties.SetName(next, "Preview next interface design");
        previous.Click += (_, _) => { _selection.Move(-1); RefreshSelection(); };
        next.Click += (_, _) => { _selection.Move(1); RefreshSelection(); };
        navigation.Children.Add(previous); navigation.Children.Add(_position); navigation.Children.Add(next);
        root.Children.Add(navigation); root.Children.Add(_title); root.Children.Add(_description); root.Children.Add(_preview);
        root.Children.Add(new TextBlock { Text = "Preview: the same files, selection, check states and disabled controls in every design.",
            TextWrapping = TextWrapping.Wrap, Opacity = .7, FontSize = 12 });
        root.Children.Add(_applyBelow);
        Content = root;
        SizeChanged += (_, e) => ArrangeGallery(e.NewSize.Width);
        Loaded += (_, _) =>
        {
            DesignRuntime.Changed -= OnAppliedChanged;
            DesignRuntime.Changed += OnAppliedChanged;
            _selection.SynchronizeApplied(_preferences.Current.Id);
            RefreshSelection(); ArrangeGallery(ActualWidth);
        };
        Unloaded += (_, _) => DesignRuntime.Changed -= OnAppliedChanged;
        RefreshSelection();
    }

    private void Apply() { _selection.Apply(_preferences); RefreshSelection(); }

    private void SetMode(DesignPickerMode mode)
    {
        _selection.Mode = mode;
        if (mode == DesignPickerMode.Gallery && _cards.Count == 0)
        {
            BuildGallery();
            RefreshSelection();
        }
        _gallery.Visibility = mode == DesignPickerMode.Gallery ? Visibility.Visible : Visibility.Collapsed;
        _openSingle.Visibility = _gallery.Visibility;
        ArrangeGallery(ActualWidth); // The exact pending preview survives mode changes.
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
            AutomationProperties.SetName(card, $"Preview {design.Name}");
            card.Click += (_, _) => { _selection.Select(design.Id); RefreshSelection(); };
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
        _selection.SynchronizeApplied(_preferences.Current.Id); RefreshSelection();
    }

    private void RefreshSelection()
    {
        var design = _selection.Preview;
        _title.Text = design.Name; _description.Text = design.Description;
        _position.Text = $"{_selection.PreviewIndex + 1} / {DesignCatalog.All.Count}";
        _applied.Text = $"Applied: {_preferences.Current.Name}" + (_selection.HasPendingChange ? $"   ·   Previewing: {design.Name}" : "");
        _showApplied.IsEnabled = _selection.HasPendingChange;
        _apply.Content = _selection.HasPendingChange ? "Apply design" : "Design applied";
        _apply.IsEnabled = _selection.HasPendingChange;
        _applyBelow.IsEnabled = _selection.HasPendingChange;
        if (_preview.Child is not DesignPreviewRoot || !Equals(_preview.Tag, design.Id))
        {
            _preview.Child = new DesignPreviewRoot(design); _preview.Tag = design.Id;
        }
        foreach (var (button, caption, id) in _cards)
        {
            var previewed = id == design.Id; var applied = id == _preferences.Current.Id;
            caption.Text = previewed && applied ? "Previewed · Applied" : previewed ? "Previewed" : applied ? "Applied" : "Select to preview";
            button.BorderBrush = previewed ? new SolidColorBrush(new Windows.UI.ViewManagement.UISettings()
                .UIElementColor(Windows.UI.ViewManagement.UIElementType.Highlight))
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            AutomationProperties.SetHelpText(button, caption.Text);
        }
    }
}
