#if ANDROID
using Android.Content;
using Android.Views.InputMethods;
#endif

namespace VideoLocalShow;

/// <summary>
/// Dismisses the on-screen keyboard. Entry.Unfocus() alone doesn't do this on Android - it
/// moves logical focus away from the control, but the soft keyboard (IME) stays open unless
/// explicitly told to hide, which requires talking to the platform's InputMethodManager.
/// </summary>
public static class KeyboardService
{
    public static void Hide(VisualElement? source = null)
    {
#if ANDROID
        // Read the native view's window token before anything unfocuses it: once a view
        // loses focus, Activity.CurrentFocus can already be null by the time this runs,
        // leaving nothing to hide the keyboard from.
        var nativeView = source?.Handler?.PlatformView as Android.Views.View;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            var activity = Platform.CurrentActivity;
            var view = nativeView ?? activity?.CurrentFocus;
            if (activity is null || view is null)
            {
                return;
            }

            if (activity.GetSystemService(Context.InputMethodService) is InputMethodManager imm)
            {
                imm.HideSoftInputFromWindow(view.WindowToken, HideSoftInputFlags.None);
            }

            view.ClearFocus();
        });
#endif
    }
}
