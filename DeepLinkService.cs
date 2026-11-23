namespace VideoLocalShow;

/// <summary>
/// Bridges an incoming Android Send/View intent (a YouTube link shared in from another app, or
/// a youtu.be link opened directly) from MainActivity into the MAUI page that knows how to act
/// on it. MainActivity has no reference to MainPage, so this is the hand-off point between them.
/// Buffered as well as event-based: the intent that launches the app cold arrives before
/// MainPage's constructor has run and subscribed, so a late subscriber still picks it up once
/// via <see cref="ConsumePendingUrl"/> instead of losing it. The two paths are mutually
/// exclusive - a url is either delivered live or buffered, never both - so a link never gets
/// processed twice: once by whichever subscriber was already listening when it arrived, and
/// again by OnAppearing's buffered pickup finding the same value still sitting there.
/// </summary>
public static class DeepLinkService
{
    public static string? PendingUrl { get; private set; }

    public static event Action<string>? LinkReceived;

    public static void Handle(string url)
    {
        if (LinkReceived is { } handler)
        {
            handler.Invoke(url);
        }
        else
        {
            PendingUrl = url;
        }
    }

    /// <summary>
    /// iOS: picks up a link the Share Extension left in the App Group, if any, and handles it.
    /// Called whenever the app comes to the front. It can't live in whichever page happens to be
    /// showing: the page that acts on shared links isn't the one the app opens on.
    /// </summary>
    public static void PollAppGroup()
    {
#if IOS
        var shared = new Foundation.NSUserDefaults("group.com.videolocalshowapp.videolocalshow", Foundation.NSUserDefaultsType.SuiteName);
        if (shared.StringForKey("SharedUrl") is { Length: > 0 } sharedUrl)
        {
            shared.RemoveObject("SharedUrl");
            ShareDebugLog.Append($"DeepLinkService.PollAppGroup found SharedUrl: {sharedUrl}");
            Handle(sharedUrl);
        }
#endif
    }

    public static string? ConsumePendingUrl()
    {
        var url = PendingUrl;
        PendingUrl = null;
        return url;
    }
}
