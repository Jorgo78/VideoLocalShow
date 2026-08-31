#if ANDROID
using System.Runtime.Versioning;
using Android.Content.PM;
using Android.Views;
// MAUI also defines a Window type, so name the Android one explicitly.
using AndroidWindow = Android.Views.Window;
#endif

namespace VideoLocalShow;

/// <summary>
/// Turns the player into a genuine full-screen experience: system bars hidden and the screen
/// rotated to landscape, where a 16:9 video actually fills the display instead of sitting in
/// a band between black margins. Restores the normal state when playback is left.
/// There is no cross-platform API for this, so it is a no-op where it isn't implemented.
/// </summary>
public static class FullScreenMode
{
    public static void Enable() => Apply(fullScreen: true);

    public static void Disable() => Apply(fullScreen: false);

    private static void Apply(bool fullScreen)
    {
#if ANDROID
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var activity = Platform.CurrentActivity;
            var window = activity?.Window;
            if (activity is null || window is null)
            {
                return;
            }

            activity.RequestedOrientation = fullScreen
                ? ScreenOrientation.SensorLandscape
                : ScreenOrientation.Portrait;

            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                ApplyWithInsetsController(window, fullScreen);
            }
            else
            {
                ApplyWithSystemUiFlags(window, fullScreen);
            }
        });
#endif
    }

    /// <summary>
    /// Re-applies the hidden system bars. Presenting a modal page re-lays out the window and
    /// brings the bars back, so the request has to be repeated once the page is settled.
    /// </summary>
    public static void Reapply()
    {
#if ANDROID
        Apply(fullScreen: true);
#endif
    }

#if ANDROID
    [SupportedOSPlatform("android30.0")]
    private static void ApplyWithInsetsController(AndroidWindow window, bool fullScreen)
    {
        // SetDecorFitsSystemWindows is marked deprecated from API 35, but its replacement
        // (edge-to-edge by default) only exists on those newer releases; this call remains
        // the correct way to opt in across the range of versions this app supports.
#pragma warning disable CA1422
        window.SetDecorFitsSystemWindows(!fullScreen);
#pragma warning restore CA1422

        var controller = window.InsetsController;
        if (controller is null)
        {
            return;
        }

        if (fullScreen)
        {
            // Keep a swipe available so the user can still reach the system bars.
            controller.SystemBarsBehavior = (int)WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
            controller.Hide(WindowInsets.Type.SystemBars());
        }
        else
        {
            controller.Show(WindowInsets.Type.SystemBars());
        }
    }

    private static void ApplyWithSystemUiFlags(AndroidWindow window, bool fullScreen)
    {
        if (window.DecorView is not { } decorView)
        {
            return;
        }

        // These flags are superseded by WindowInsetsController from API 30, which the branch
        // above already uses; this path only ever runs on older releases where they are current.
#pragma warning disable CA1422
        decorView.SystemUiFlags = fullScreen
            ? SystemUiFlags.ImmersiveSticky
                | SystemUiFlags.Fullscreen
                | SystemUiFlags.HideNavigation
                | SystemUiFlags.LayoutStable
                | SystemUiFlags.LayoutFullscreen
                | SystemUiFlags.LayoutHideNavigation
            : SystemUiFlags.Visible;
#pragma warning restore CA1422
    }
#endif
}
