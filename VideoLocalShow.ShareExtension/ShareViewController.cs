using Foundation;
using Social;
using UIKit;

namespace VideoLocalShow.ShareExtension;

// This is the whole UI iOS shows in the "Condividi" sheet once VideoLocalShow is picked -
// SLComposeServiceViewController is Apple's own built-in compose screen (a text box plus a
// "Posta"/Post button), the same base class every well-behaved Share Extension uses, instantiated
// here via MainInterface.storyboard (see Info.plist's NSExtensionMainStoryboard) rather than a
// direct class reference - that direct-reference approach never actually launched on a physical
// device despite working from a plain UIViewController subclass in the Simulator.
//
// The extension runs in its own separate, sandboxed process from the main app - there is no
// direct way to call into the main app's code from here - so the hand-off goes through an App
// Group container both processes can see, and the main app is "woken" via its own private URL
// scheme (see AppDelegate.OpenUrl) to go read what was left there.
[Register("ShareViewController")]
public partial class ShareViewController : SLComposeServiceViewController
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
    }

    // Called by SLComposeServiceViewController as soon as the share sheet's content is ready to
    // validate - returning true keeps the built-in "Posta" button enabled, which is what
    // actually triggers DidSelectPost below once the user taps it.
    public override bool IsContentValid() => true;

    // Fires once the user taps "Posta" on the built-in compose screen - this is where the
    // actual hand-off to the main app happens.
    public override void DidSelectPost()
    {
        _ = HandleShareAsync();
    }

    public override SLComposeSheetConfigurationItem[] GetConfigurationItems() => [];

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

                OpenHostApp(this, new NSUrl("videolocalshow://share"));
                AppendLog("OpenHostApp chiamato.");
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

    // Walking the responder chain to find and call the host app's own UIApplication.OpenUrl -
    // the technique established for Share Extensions in practice, since
    // UIApplication.SharedApplication is nil in an extension process.
    private static void OpenHostApp(UIResponder startingFrom, NSUrl url)
    {
        UIResponder? responder = startingFrom;
        while (responder is not null)
        {
            if (responder is UIApplication app)
            {
#pragma warning disable CA1422 // deprecated in favor of the completion-handler overload, but
                               // that overload requires a non-null options dictionary and this
                               // simpler one is the one actually documented to work when called
                               // this way from within an extension.
                app.OpenUrl(url);
#pragma warning restore CA1422
                return;
            }

            responder = responder.NextResponder;
        }

        AppendLog("UIApplication non trovata nella responder chain.");
    }

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
