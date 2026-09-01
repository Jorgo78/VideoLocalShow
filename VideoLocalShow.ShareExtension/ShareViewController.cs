using CoreGraphics;
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

    // TEMP DIAGNOSTIC - remove once sharing is confirmed reliably working end to end. Two
    // separate open attempts silently produced no observable difference, which points at the
    // failure being further upstream (extraction, or a swallowed exception) rather than in
    // either open mechanism - showing status directly on this screen, instead of relying on a
    // successful app-open to ever happen, is the fastest way to actually see what's going on.
    private UILabel? _statusLabel;

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();

        View!.BackgroundColor = UIColor.Black;
        _statusLabel = new UILabel(new CGRect(20, 60, View.Bounds.Width - 40, 400))
        {
            Lines = 0,
            TextColor = UIColor.White,
            Font = UIFont.SystemFontOfSize(13),
            Text = "Avvio..."
        };
        View.AddSubview(_statusLabel);

        _ = HandleShareAsync();
    }

    private void SetStatus(string text)
    {
        InvokeOnMainThread(() =>
        {
            if (_statusLabel is not null)
            {
                _statusLabel.Text += "\n" + text;
            }
        });
    }

    private async Task HandleShareAsync()
    {
        try
        {
            SetStatus($"InputItems: {ExtensionContext?.InputItems?.Length ?? -1}");

            var link = await ExtractSharedTextOrUrlAsync();
            SetStatus($"Link estratto: {link ?? "(nessuno)"}");

            if (!string.IsNullOrWhiteSpace(link))
            {
                var shared = new NSUserDefaults(AppGroupId, NSUserDefaultsType.SuiteName);
                shared.SetString(link, SharedUrlKey);
                shared.Synchronize();
                SetStatus("Scritto in App Group.");

                OpenHostApp(this, new NSUrl("videolocalshow://share"));
                SetStatus("OpenHostApp chiamato.");
            }
        }
        catch (Exception ex)
        {
            SetStatus($"ERRORE: {ex}");
        }

        // Held open for a few seconds instead of completing immediately, purely so the status
        // text above has time to actually be read - not the final behavior.
        await Task.Delay(6000);
        ExtensionContext?.CompleteRequest([], null);
    }

    // NSExtensionContext.OpenUrl - the API that looks purpose-built for this - is documented as
    // being for Today/widget extensions specifically; it did not reliably wake the containing
    // app when tried here. Walking the responder chain to find and call the host app's own
    // UIApplication.OpenUrl directly is the technique actually established for Share
    // Extensions in practice. It's synchronous, so - unlike the previous attempt - there is no
    // async completion to race against CompleteRequest tearing the extension down afterward.
    private void OpenHostApp(UIResponder startingFrom, NSUrl url)
    {
        // UIApplication.SharedApplication is exactly what's unavailable here - nil in an
        // extension process - which is the whole reason this walks up from a live view
        // controller's own place in the responder chain instead of starting from there.
        UIResponder? responder = startingFrom;
        var depth = 0;
        while (responder is not null)
        {
            depth++;
            if (responder is UIApplication app)
            {
                SetStatus($"UIApplication trovata a profondità {depth}.");
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

        SetStatus($"UIApplication NON trovata (profondità esplorata: {depth}).");
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
                var types = string.Join(", ", attachment.RegisteredTypeIdentifiers ?? []);
                SetStatus($"Attachment tipi: {types}");

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
