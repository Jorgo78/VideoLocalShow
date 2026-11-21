using Foundation;
using UIKit;

namespace VideoLocalShow;

// Apps built with the iOS 27 SDK must use the UIKit scene lifecycle or they crash at launch on
// iOS 27 (see the UIApplicationSceneManifest in Info.plist, which points at this class by its
// registered name). MauiUISceneDelegate does all the window setup; what this adds is catching
// the wake-up URL the Share Extension opens (videolocalshow://), which with scenes is delivered
// here rather than to AppDelegate.OpenUrl.
[Register("SceneDelegate")]
public class SceneDelegate : MauiUISceneDelegate
{
	public override bool OpenUrl(UIScene scene, NSSet<UIOpenUrlContext> urlContexts)
	{
		var handled = false;

		foreach (var context in urlContexts.ToArray<UIOpenUrlContext>())
		{
			ShareDebugLog.Append($"SceneDelegate.OpenUrl called, scheme={context.Url.Scheme}");
			handled = AppDelegate.HandleWakeUpUrl(context.Url) || handled;
		}

		return handled || base.OpenUrl(scene, urlContexts);
	}
}
