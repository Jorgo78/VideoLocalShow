using Foundation;
using UIKit;

namespace VideoLocalShow;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

	// The Share Extension runs as its own separate process (a `.appex`, sandboxed apart from
	// this app) - it can't call into DeepLinkService directly, so it drops the shared link into
	// an App Group container the two share, then "wakes" this app via its own private URL
	// scheme just to get it running again. This is where that wake-up is caught: pull the link
	// back out of the shared container and feed it into the exact same DeepLinkService pathway
	// Android's share intent already uses.
	public override bool OpenUrl(UIApplication app, NSUrl url, NSDictionary options)
	{
		// TEMP DIAGNOSTIC - remove once iOS sharing is confirmed reliably working end to end.
		ShareDebugLog.Append($"AppDelegate.OpenUrl called, scheme={url.Scheme}");

		if (url.Scheme == "videolocalshow")
		{
			var shared = new NSUserDefaults("group.com.videolocalshowapp.videolocalshow", NSUserDefaultsType.SuiteName);
			var sharedUrl = shared.StringForKey("SharedUrl");
			ShareDebugLog.Append($"SharedUrl from App Group: {sharedUrl ?? "(null)"}");

			if (!string.IsNullOrEmpty(sharedUrl))
			{
				shared.RemoveObject("SharedUrl");
				DeepLinkService.Handle(sharedUrl);
				ShareDebugLog.Append("DeepLinkService.Handle called.");
			}

			return true;
		}

		return base.OpenUrl(app, url, options);
	}
}
