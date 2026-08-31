using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace VideoLocalShow;

// Exported = true is required from Android 12 (API 31) onward for any activity that declares
// intent filters - without it the system refuses to route the share/view intents below to us
// at all, MainLauncher notwithstanding.
// SingleTask (not SingleTop): a share intent from another app opens a brand new task/window
// under SingleTop, since "top of the same task" never applies coming from a different app's
// task - the user ends up with two separate app windows, watching the old one while the share
// silently lands (and downloads) in a new one they never see. SingleTask guarantees there is
// only ever one instance of this Activity system-wide - a second launch from anywhere, shared
// or otherwise, is routed to the existing instance via OnNewIntent and that task is brought to
// the front, instead of spawning another. It also happens to be what fixes Shell.Current's
// "unable to determine the current Shell instance" exception, which only occurs when more than
// one window/task is alive to be ambiguous between.
[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTask, Exported = true, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
// Lets this app appear in another app's "Condividi" (Share) sheet for plain text - this is how
// sharing a video straight from the YouTube app, or a browser, reaches us.
[IntentFilter([Intent.ActionSend], Categories = [Intent.CategoryDefault], DataMimeType = "text/plain")]
// Also offers this app as an "apri con" option for youtube.com / youtu.be links tapped directly
// elsewhere (a chat message, a browser address bar, ...) - one filter per host, since
// IntentFilterAttribute only takes a single DataHost.
[IntentFilter([Intent.ActionView], Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable], DataSchemes = ["http", "https"], DataHost = "youtu.be")]
[IntentFilter([Intent.ActionView], Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable], DataSchemes = ["http", "https"], DataHost = "www.youtube.com")]
[IntentFilter([Intent.ActionView], Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable], DataSchemes = ["http", "https"], DataHost = "youtube.com")]
[IntentFilter([Intent.ActionView], Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable], DataSchemes = ["http", "https"], DataHost = "m.youtube.com")]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        // MauiAppCompatActivity.OnCreate builds the whole MAUI app (Application, Shell,
        // MainPage) synchronously, so by the time it returns here MainPage already exists and
        // has subscribed to DeepLinkService - a cold start via "Condividi" reaches it too, not
        // just a share received while the app is already running.
        base.OnCreate(savedInstanceState);
        HandleIncomingIntent(Intent);
    }

    // LaunchMode.SingleTask means any later launch - a second share, tapping the app icon again,
    // opening another youtube.com link - is routed to this one running instance rather than
    // spinning up a new task, and arrives here instead of through OnCreate.
    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        HandleIncomingIntent(intent);
    }

    private static void HandleIncomingIntent(Intent? intent)
    {
        var url = intent?.Action switch
        {
            Intent.ActionSend => intent.GetStringExtra(Intent.ExtraText),
            Intent.ActionView => intent.DataString,
            _ => null
        };

        if (!string.IsNullOrWhiteSpace(url))
        {
            DeepLinkService.Handle(url);
        }
    }
}
