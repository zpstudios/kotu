using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KOTU.App.Controls;

// This is deliberately not an ExplorerEntryVm: navigation has no file/check/workset identity.
public sealed class ExplorerParentTile : INotifyPropertyChanged
{
    private bool _canNavigate;
    public bool CanNavigate
    {
        get => _canNavigate;
        internal set
        {
            if (_canNavigate == value) return;
            _canNavigate = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanNavigate)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class ExplorerTileTemplateSelector : DataTemplateSelector
{
    public DataTemplate FileTemplate { get; set; } = null!;
    public DataTemplate ParentTemplate { get; set; } = null!;
    protected override DataTemplate SelectTemplateCore(object item) =>
        item is ExplorerParentTile ? ParentTemplate : FileTemplate;
    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}
