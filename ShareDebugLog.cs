#if IOS
using Foundation;
#endif

namespace VideoLocalShow;

// TEMP DIAGNOSTIC - remove once iOS sharing is confirmed reliably working end to end. Both this
// app and its Share Extension (a separate, sandboxed process - see
// VideoLocalShow.ShareExtension/ShareViewController.cs) write to the same file here, inside the
// App Group container they share, so what actually happened during a share attempt can be read
// back from the app's own "Log" tab afterward - far more reliable than trying to read text off
// the extension's own screen during the couple of seconds before it dismisses itself, or relying
// on a screen recording.
public static class ShareDebugLog
{
    private const string AppGroupId = "group.com.videolocalshowapp.videolocalshow";
    private const string LogFileName = "sharelog.txt";

#if IOS
    private static string? LogFilePath =>
        NSFileManager.DefaultManager
            .GetContainerUrl(AppGroupId)
            ?.Append(LogFileName, false)
            .Path;
#endif

    public static string Read()
    {
#if IOS
        try
        {
            var path = LogFilePath;
            if (path is null || !File.Exists(path))
            {
                return "(nessun log ancora)";
            }

            return File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            return $"Errore lettura log: {ex}";
        }
#else
        return "Il log di condivisione è disponibile solo su iOS.";
#endif
    }

    // Lets the main app add its own entries to the same log the extension writes to, so the
    // whole flow - extension ran, app woke up, link handed to DeepLinkService, search/download
    // started - shows up in one place regardless of which tab happens to be open when it does.
    public static void Append(string message)
    {
#if IOS
        try
        {
            var path = LogFilePath;
            if (path is not null)
            {
                File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} [App] {message}\n");
            }
        }
        catch
        {
            // Best-effort logging only.
        }
#endif
    }

    public static void Clear()
    {
#if IOS
        try
        {
            var path = LogFilePath;
            if (path is not null && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort only.
        }
#endif
    }
}
