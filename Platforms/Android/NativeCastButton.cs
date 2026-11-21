using Android.Content;
using Android.Util;
using Android.Views;
using Android.Widget;
using AndroidX.Media3.UI;
using AView = Android.Views.View;

namespace VideoLocalShow;

/// <summary>
/// Adds a "share to another screen" button to the player's own native control bar. The player's
/// controls (Media3's PlayerControlView) have no cast button of their own, so this adds one to
/// the bar's main row of icons, next to settings and full screen, styled like its neighbours,
/// instead of a separate button of ours floating over the video.
///
/// Without the Google Cast SDK the button opens the system's Cast / Smart View screen, where the
/// phone's display can be mirrored to a TV.
/// </summary>
public static class NativeCastButton
{
    private const string ButtonTag = "vls_cast_button";

    /// <summary>
    /// Looks for the player's control bar inside the MediaElement's native view and adds the
    /// button to it. Returns false if the bar isn't there (yet), so the caller can try again.
    /// </summary>
    public static bool TryAttach(object? mediaElementPlatformView)
    {
        if (mediaElementPlatformView is not ViewGroup root)
        {
            return false;
        }

        if (FindPlayerView(root) is not { } playerView)
        {
            return false;
        }

        var context = playerView.Context;
        var resources = context?.Resources;
        if (context is null || resources is null)
        {
            return false;
        }

        // Where the button goes: the main row of the control bar, right before the settings
        // button - the row of icons the user actually sees. (The "extra controls" slot is only
        // revealed by tapping the bar's overflow chevron; the first attempt put the button there
        // and it never showed, since that container stays hidden.)
        var rowId = resources.GetIdentifier("exo_basic_controls", "id", context.PackageName);
        var settingsId = resources.GetIdentifier("exo_settings", "id", context.PackageName);
        if (rowId == 0 || playerView.FindViewById(rowId) is not ViewGroup row)
        {
            return false;
        }

        // Already added (the page can ask again each time playback starts).
        if (row.FindViewWithTag(new Java.Lang.String(ButtonTag)) is not null)
        {
            return true;
        }

        var iconId = resources.GetIdentifier("ic_cast", "drawable", context.PackageName);
        if (iconId == 0)
        {
            return false;
        }

        // Same size as the player's own buttons: a 48dp touch target around a 24dp icon.
        var size = Dp(resources, 48);
        var padding = Dp(resources, 12);

        // Fully qualified: MAUI has its own ImageButton type, which a plain name would also match.
        var button = new Android.Widget.ImageButton(context)
        {
            ContentDescription = "Condividi su un altro schermo",
            Tag = new Java.Lang.String(ButtonTag)
        };
        button.SetImageResource(iconId);
        button.SetBackgroundColor(Android.Graphics.Color.Transparent);
        button.SetScaleType(ImageView.ScaleType.FitCenter);
        button.SetPadding(padding, padding, padding, padding);
        button.Click += (_, _) => OpenCastSettings();

        // Before the settings button if it is in this row, otherwise at the start of the row.
        var index = 0;
        if (settingsId != 0 && playerView.FindViewById(settingsId) is { } settings)
        {
            var settingsIndex = row.IndexOfChild(settings);
            index = settingsIndex >= 0 ? settingsIndex : 0;
        }

        row.AddView(button, index, new LinearLayout.LayoutParams(size, size));
        return true;
    }

    private static PlayerView? FindPlayerView(AView view)
    {
        if (view is PlayerView playerView)
        {
            return playerView;
        }

        if (view is ViewGroup group)
        {
            for (var i = 0; i < group.ChildCount; i++)
            {
                if (group.GetChildAt(i) is { } child && FindPlayerView(child) is { } found)
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static int Dp(Android.Content.Res.Resources resources, float dp) =>
        (int)TypedValue.ApplyDimension(ComplexUnitType.Dip, dp, resources.DisplayMetrics);

    // Playback carries on while the settings screen is open, thanks to the background media service.
    private static void OpenCastSettings()
    {
        var activity = Platform.CurrentActivity;
        if (activity is null)
        {
            return;
        }

        try
        {
            activity.StartActivity(new Intent(Android.Provider.Settings.ActionCastSettings));
        }
        catch (ActivityNotFoundException)
        {
            // A device without a dedicated cast screen: the connections settings is the nearest
            // place from which screen mirroring can be reached.
            try
            {
                activity.StartActivity(new Intent(Android.Provider.Settings.ActionWirelessSettings));
            }
            catch (ActivityNotFoundException)
            {
                _ = CommunityToolkit.Maui.Alerts.Toast.Make(
                    "Questo telefono non permette di condividere lo schermo da qui. Usa il pannello rapido (Smart View / Cast).",
                    CommunityToolkit.Maui.Core.ToastDuration.Long).Show();
            }
        }
    }
}
