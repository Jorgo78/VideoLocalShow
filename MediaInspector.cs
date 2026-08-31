#if ANDROID
using Android.Media;
#elif IOS || MACCATALYST
using AVFoundation;
using Foundation;
#endif

namespace VideoLocalShow;

public static class MediaInspector
{
    /// <summary>
    /// Reports whether a local media file actually carries a video track.
    /// This inspects the file's own track list rather than asking the player: a player's
    /// reported dimensions are still zero when playback has only just been opened, which
    /// makes them useless for telling a real video apart from an audio-only download.
    /// </summary>
    public static async Task<bool> HasVideoTrackAsync(string filePath)
    {
        try
        {
#if ANDROID
            // Reading the container touches the disk, so keep it off the UI thread.
            return await Task.Run(() =>
            {
                using var extractor = new MediaExtractor();
                extractor.SetDataSource(filePath);

                for (var i = 0; i < extractor.TrackCount; i++)
                {
                    var mime = extractor.GetTrackFormat(i)?.GetString(MediaFormat.KeyMime);
                    if (mime is not null && mime.StartsWith("video/", StringComparison.Ordinal))
                    {
                        return true;
                    }
                }

                return false;
            });
#elif IOS || MACCATALYST
            using var asset = new AVUrlAsset(NSUrl.FromFilename(filePath));
            var videoTracks = await asset.LoadTracksWithMediaTypeAsync(AVMediaTypes.Video.GetConstant()!);
            return videoTracks.Count > 0;
#else
            await Task.CompletedTask;
            return true;
#endif
        }
        catch (Exception)
        {
            // If the file can't be inspected, assume it has video so the player is at least
            // given a chance rather than being pre-empted by a wrong "audio only" message.
            return true;
        }
    }
}
