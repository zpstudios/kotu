using KOTU.Core.Contracts;
using KOTU.Core.Settings;

namespace KOTU.Module.Record;

public sealed class RecordModule(ISettingsService settings) : IModule
{
    private readonly RecordingFolderCoordinator _folders = new(settings);
    public string Id => "record";
    public string DisplayName => "Record";
    public string BrandName => "KOTU-record";
    public string IconGlyph => "\uE7C8";
    public IReadOnlyList<string> SupportedExtensions => [];
    public bool RegistersFileAssociations => false;
    public object CreateView(OpenContext context) => new RecordView(settings, _folders);
}
