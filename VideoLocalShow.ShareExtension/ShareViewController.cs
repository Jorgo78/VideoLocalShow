using Foundation;
using UIKit;

namespace VideoLocalShow.ShareExtension;

// This is the whole UI iOS shows in the "Condividi" sheet once VideoLocalShow is picked - it
// never actually shows its own screen; it grabs whatever was shared, hands it off, and dismisses
// itself immediately. The extension runs in its own separate, sandboxed process from the main
// app - there is no direct way to call into the main app's code from here - so the hand-off goes
// through an App Group container both processes can see, and the main app is "woken" via its own
// private URL scheme (see AppDelegate.OpenUrl) to go read what was left there.
[Register("ShareViewController")]
public class ShareViewController : UIViewController
{
    private const string AppGroupId = "group.com.videolocalshowapp.videolocalshow";
    private const string SharedUrlKey = "SharedUrl";

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        _ = HandleShareAsync();
    }

    private async Task HandleShareAsync()
    {
        try
        {
            var link = await ExtractSharedTextOrUrlAsync();
            if (!string.IsNullOrWhiteSpace(link))
            {
                var shared = new NSUserDefaults(AppGroupId, NSUserDefaultsType.SuiteName);
                shared.SetString(link, SharedUrlKey);
                shared.Synchronize();

                // Asks the system to relay this to the containing app, rather than trying to
                // reach it directly - an extension process has no way to do that on its own.
                // The app only actually launches/foregrounds once this extension's own request
                // is completed below, so this is fired first and awaited via its own handler
                // rather than blocking on it.
                ExtensionContext?.OpenUrl(new NSUrl("videolocalshow://share"), completed => { });
            }
        }
        catch
        {
            // A share that can't be parsed just closes the sheet quietly below, rather than
            // leaving the user stuck looking at a blank extension screen.
        }
        finally
        {
            ExtensionContext?.CompleteRequest([], null);
        }
    }

    private async Task<string?> ExtractSharedTextOrUrlAsync()
    {
        var items = ExtensionContext?.InputItems;
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
}
