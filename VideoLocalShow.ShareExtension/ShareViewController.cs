using Foundation;
using UIKit;

namespace VideoLocalShow.ShareExtension;

// This is the whole UI iOS shows in the "Condividi" sheet once VideoLocalShow is picked - it
// never actually shows its own screen; it grabs whatever was shared, hands it off, and dismisses
// itself immediately, no tap required. Instantiated via MainInterface.storyboard (see Info.plist's
// NSExtensionMainStoryboard) rather than a direct NSExtensionPrincipalClass reference - that
// direct-reference approach never actually launched on a physical device despite working in the
// Simulator.
//
// The extension runs in its own separate, sandboxed process from the main app - there is no
// direct way to call into the main app's code from here - so the hand-off goes through an App
// Group container both processes can see. Waking the main app from here is best effort only -
// Apple documents NSExtensionContext.OpenUrl as being for Today/widget extensions specifically,
// not Share Extensions (see OpenHostApp below for what is tried instead). Whether or not that
// wake-up works, MainPage also polls the same App Group container for a pending link every time
// it appears (including the app simply being brought back to the foreground) - so the worst
// case is: share here, then switch back to VideoLocalShow, and it picks the link up right then.
[Register("ShareViewController")]
public partial class ShareViewController : UIViewController
{
    private const string AppGroupId = "group.com.videolocalshowapp.videolocalshow";
    private const string SharedUrlKey = "SharedUrl";

    // Required by the storyboard-based instantiation pattern - the Objective-C runtime creates
    // this controller by handing back a native pointer, not by calling a normal parameterless
    // C# constructor. This ctor should do no real work itself, matching Apple's own guidance.
    protected ShareViewController(IntPtr handle) : base(handle)
    {
    }

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        AppendLog("ViewDidLoad entered (storyboard-based).");

        // No visible UI - the whole point is that sharing feels instant, not a screen to look at.
        View!.BackgroundColor = UIColor.Clear;

        _ = HandleShareAsync();
    }

    private async Task HandleShareAsync()
    {
        try
        {
            var link = await ExtractSharedTextOrUrlAsync();
            AppendLog($"Link estratto: {link ?? "(nessuno)"}");

            if (!string.IsNullOrWhiteSpace(link))
            {
                var shared = new NSUserDefaults(AppGroupId, NSUserDefaultsType.SuiteName);
                shared.SetString(link, SharedUrlKey);
                shared.Synchronize();
                AppendLog("Scritto in App Group.");

                // Best effort: if this brings the app to the front, AppDelegate.OpenUrl picks the
                // link up straight away; if not, MainPage still finds it when next opened.
                var opened = OpenHostApp(this, new NSUrl("videolocalshow://share"));
                AppendLog($"OpenHostApp: {(opened ? "richiesta inviata" : "UIApplication non trovata")}");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"ERRORE: {ex}");
        }
        finally
        {
            ExtensionContext?.CompleteRequest([], null);
        }
    }

    // Asks the host app's UIApplication to open our own URL scheme, which launches/foregrounds
    // VideoLocalShow. NSExtensionContext.OpenUrl is only honoured for Today/widget extensions,
    // and the older UIApplication.openURL: that an earlier attempt called is a silent no-op on
    // iOS 18+. What still works there is the newer openURL:options:completionHandler:, reached
    // by walking up the responder chain to the host's UIApplication (UIApplication.SharedApplication
    // is unavailable inside an extension) and sending that message directly - the C# binding for
    // it can't be used because it is marked unavailable to extensions. This relies on behaviour
    // Apple doesn't officially document for Share Extensions, so it may stop working in a future
    // iOS release; the App Group hand-off above is what keeps sharing working regardless.
    private static bool OpenHostApp(UIResponder startingFrom, NSUrl url)
    {
        var selector = ObjCRuntime.Selector.GetHandle("openURL:options:completionHandler:");

        UIResponder? responder = startingFrom;
        while (responder is not null)
        {
            if (responder is UIApplication app)
            {
                using var options = new NSDictionary();
                SendOpenUrl(app.Handle, selector, url.Handle, options.Handle, IntPtr.Zero);
                return true;
            }

            responder = responder.NextResponder;
        }

        return false;
    }

    [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendOpenUrl(IntPtr receiver, IntPtr selector, IntPtr url, IntPtr options, IntPtr completionHandler);

    private async Task<string?> ExtractSharedTextOrUrlAsync()
    {
        var items = ExtensionContext?.InputItems;
        AppendLog($"InputItems: {items?.Length ?? -1}");
        if (items is null)
        {
            return null;
        }

        foreach (var item in items.OfType<NSExtensionItem>())
        {
            if (item.Attachments is null)
            {
                continue;
            }

            foreach (var attachment in item.Attachments)
            {
                var types = string.Join(", ", attachment.RegisteredTypeIdentifiers ?? []);
                AppendLog($"Attachment tipi: {types}");

                if (attachment.HasItemConformingTo("public.url"))
                {
                    var loaded = await attachment.LoadItemAsync("public.url", null);
                    if (loaded is NSUrl url)
                    {
                        return url.AbsoluteString;
                    }
                }
                else if (attachment.HasItemConformingTo("public.plain-text"))
                {
                    var loaded = await attachment.LoadItemAsync("public.plain-text", null);
                    if (loaded is NSString text)
                    {
                        return text.ToString();
                    }
                }
            }
        }

        return null;
    }

    // TEMP DIAGNOSTIC - remove once iOS sharing is confirmed reliably working end to end.
    // Appends straight to the same file the main app's "Log" tab reads (ShareDebugLog.cs), via
    // the App Group container both processes can see - kept as a small standalone copy here
    // rather than referencing that class, since this project only targets net10.0-ios while the
    // main app project targets several platforms.
    private static void AppendLog(string message)
    {
        try
        {
            var containerUrl = NSFileManager.DefaultManager.GetContainerUrl(AppGroupId);
            var path = containerUrl?.Append("sharelog.txt", false).Path;
            if (path is not null)
            {
                File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {message}\n");
            }
        }
        catch
        {
            // Best-effort logging only.
        }
    }
}
