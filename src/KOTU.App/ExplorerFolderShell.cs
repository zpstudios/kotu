using KOTU.Core.Routing;
using Windows.Storage;

namespace KOTU.App;

/// <summary>A typed folder shell request; no command line, interpreter or directory enumeration.</summary>
internal static class ExplorerFolderShell
{
    internal static Task<StorageFolder> ResolveAsync(ExplorerListing.Entry entry) => Task.Run(async () =>
    {
        var error = ExplorerMenuValidation.Validate([entry], ExplorerMenuValidation.ReadIdentity);
        if (error is not null) throw new IOException(error);
        if (!entry.IsFolder) throw new IOException("The item is no longer a folder");
        return await StorageFolder.GetFolderFromPathAsync(entry.Path);
    });

    internal static async Task<string?> OpenAsync(StorageFolder folder, ExplorerListing.Entry entry, Func<Task<bool>> canLaunch)
    {
        try
        {
            var error = await Task.Run(() => ExplorerMenuValidation.Validate([entry], ExplorerMenuValidation.ReadIdentity));
            if (error is not null) return error;
            if (!await canLaunch()) return null;
            // LaunchFolderAsync requires the calling UI/ASTA thread. Only metadata runs on a worker.
            return await Windows.System.Launcher.LaunchFolderAsync(folder)
                ? null : "Windows Explorer could not open this folder";
        }
        catch (Exception ex) { return "Could not open Windows Explorer - " + ex.Message; }
    }
}
