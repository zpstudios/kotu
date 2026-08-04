using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using WinUtil.Core.Contracts;

namespace WinUtil.Module.Hardware;

/// <summary>하드웨어 스펙 화면. WMI 수집은 느릴 수 있어 백그라운드에서 읽고, 전체 복사를 지원한다.</summary>
public sealed partial class HardwareView : UserControl
{
    private IReadOnlyList<HardwareSection> _sections = [];
    private bool _loadedOnce;

    public HardwareView(OpenContext context)
    {
        _ = context; // 파일 컨텍스트 없음
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            await RefreshAsync();
        };
    }

    private async Task RefreshAsync()
    {
        Busy.IsActive = true;
        RefreshButton.IsEnabled = false;
        try
        {
            _sections = await Task.Run(HardwareInfoService.Collect);
            Render();
        }
        finally
        {
            Busy.IsActive = false;
            RefreshButton.IsEnabled = true;
        }
    }

    private void Render()
    {
        Root.Children.Clear();
        foreach (var section in _sections)
        {
            Root.Children.Add(new TextBlock
            {
                Text = section.Title,
                FontSize = 18,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 16, 0, 6),
            });

            foreach (var item in section.Items)
            {
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var label = new TextBlock
                {
                    Text = item.Label,
                    Opacity = 0.65,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 2, 12, 2),
                };
                var value = new TextBlock
                {
                    Text = item.Value,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                    Margin = new Thickness(0, 2, 0, 2),
                };
                Grid.SetColumn(value, 1);
                grid.Children.Add(label);
                grid.Children.Add(value);
                Root.Children.Add(grid);
            }
        }
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    /// <summary>모든 섹션을 텍스트로 클립보드에 복사 (사양 공유용).</summary>
    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder();
        foreach (var section in _sections)
        {
            sb.AppendLine($"[{section.Title}]");
            foreach (var item in section.Items)
                sb.AppendLine($"{item.Label}: {item.Value}");
            sb.AppendLine();
        }

        var package = new DataPackage();
        package.SetText(sb.ToString().TrimEnd());
        Clipboard.SetContent(package);
    }
}
