using Foundation;
using UIKit;

namespace VideoLocalShow;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

	// iOS only keeps audio playing in the background (and honours the silent switch the way a
	// media player should) for an audio session in the Playback category - the default category
	// is silenced as soon as the app leaves the screen. Only the category is set here, not the
	// session activated, so launching the app doesn't interrupt music playing in another app;
	// the player activates the session itself when playback actually starts.
	public override bool FinishedLaunching(UIApplication application, NSDictionary launchOptions)
	{
		AVFoundation.AVAudioSession.SharedInstance().SetCategory(AVFoundation.AVAudioSessionCategory.Playback, out _);
		return base.FinishedLaunching(application, launchOptions);
	}

	// The Share Extension runs as its own separate process (a `.appex`, sandboxed apart from
	// this app) - it can't call into DeepLinkService directly, so it drops the shared link into
	// an App Group container the two share, then "wakes" this app via its own private URL
	// scheme just to get it running again. This is where that wake-up is caught: pull the link
	// back out of the shared container and feed it into the exact same DeepLinkService pathway
	// Android's share intent already uses.
	//
	// With the UIScene lifecycle (required by the iOS 27 SDK) iOS delivers URL opens to the scene
	// delegate instead - see SceneDelegate.OpenUrl. Both end up in HandleWakeUpUrl below, so this
	// override only matters on an OS version that still routes the open through the app delegate.
	public override bool OpenUrl(UIApplication app, NSUrl url, NSDictionary options)
	{
		// TEMP DIAGNOSTIC - remove once iOS sharing is confirmed reliably working end to end.
		ShareDebugLog.Append($"AppDelegate.OpenUrl called, scheme={url.Scheme}");

		return HandleWakeUpUrl(url) || base.OpenUrl(app, url, options);
	}

	/// <summary>
	/// Picks up the link the Share Extension left in the App Group when it opened this app's own
	/// URL scheme, and hands it to <see cref="DeepLinkService"/>. Returns false for any other URL.
	/// </summary>
	internal static bool HandleWakeUpUrl(NSUrl url)
	{
		if (url.Scheme != "videolocalshow")
		{
			return false;
		}

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
}
