using System.Security.Cryptography;
using System.Text;
// Android.Graphics and Android.Media define their own Path and Encoding types, so pin these
// two names to the framework versions used throughout this file.
using Path = System.IO.Path;
using Encoding = System.Text.Encoding;
#if ANDROID
using Android.Graphics;
using Android.Media;
#elif IOS || MACCATALYST
using AVFoundation;
using CoreMedia;
using Foundation;
using UIKit;
#endif

namespace VideoLocalShow;

/// <summary>
/// Extracts a still frame from a downloaded video to use as its thumbnail in the list.
/// Generated images are cached on disk so the frame is only decoded once per file.
/// </summary>
public static class ThumbnailProvider
{
    // Grab a frame a little way into the video rather than at the very start: openings are
    // often a blank fade-in, which would produce an empty-looking thumbnail.
    private const double FramePositionFraction = 0.1;
    private const long MinFrameTimeMicroseconds = 1_000_000;
    private const long MaxFrameTimeMicroseconds = 30_000_000;

    private static long PickFrameTime(long durationMicroseconds)
    {
        if (durationMicroseconds <= 0)
        {
            return MinFrameTimeMicroseconds;
        }

        var target = (long)(durationMicroseconds * FramePositionFraction);
        return Math.Clamp(target, MinFrameTimeMicroseconds, MaxFrameTimeMicroseconds);
    }

    public static async Task<string?> GetThumbnailAsync(string videoPath)
    {
        try
        {
            if (!File.Exists(videoPath))
            {
                return null;
            }

            var cachePath = GetCachePath(videoPath);
            if (File.Exists(cachePath))
            {
                return cachePath;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            return await GenerateAsync(videoPath, cachePath);
        }
        catch (Exception)
        {
            // A missing thumbnail is not worth failing the list over - the row simply falls
            // back to its placeholder.
            return null;
        }
    }

    private static string GetCachePath(string videoPath)
    {
        // Key the cache on path plus last-write time so a re-downloaded file gets a fresh frame.
        var info = new FileInfo(videoPath);
        var key = $"{videoPath}|{info.LastWriteTimeUtc.Ticks}|{info.Length}";
        var hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(FileSystem.CacheDirectory, "thumbnails", $"{hash}.jpg");
    }

#if ANDROID
    private static Task<string?> GenerateAsync(string videoPath, string cachePath)
    {
        return Task.Run<string?>(() =>
        {
            using var retriever = new MediaMetadataRetriever();
            retriever.SetDataSource(videoPath);

            long.TryParse(retriever.ExtractMetadata(MetadataKey.Duration), out var durationMs);
            using var frame = retriever.GetFrameAtTime(PickFrameTime(durationMs * 1000));
            if (frame is null)
            {
                return null;
            }

            using (var output = File.Create(cachePath))
            {
                frame.Compress(Bitmap.CompressFormat.Jpeg!, 80, output);
            }

            return cachePath;
        });
    }
#elif IOS || MACCATALYST
    private static Task<string?> GenerateAsync(string videoPath, string cachePath)
    {
        return Task.Run<string?>(() =>
        {
            using var asset = new AVUrlAsset(NSUrl.FromFilename(videoPath));
            using var generator = new AVAssetImageGenerator(asset)
            {
                AppliesPreferredTrackTransform = true
            };

            var durationMicroseconds = (long)(asset.Duration.Seconds * 1_000_000);
            using var image = generator.CopyCGImageAtTime(
                new CMTime(PickFrameTime(durationMicroseconds), 1_000_000),
                out _,
                out var error);

            if (image is null || error is not null)
            {
                return null;
            }

            using var uiImage = UIImage.FromImage(image);
            using var jpeg = uiImage.AsJPEG(0.8f);
            if (jpeg is null)
            {
                return null;
            }

            return jpeg.Save(cachePath, true) ? cachePath : null;
        });
    }
#else
    private static Task<string?> GenerateAsync(string videoPath, string cachePath) => Task.FromResult<string?>(null);
#endif
}
