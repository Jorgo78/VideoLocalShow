#if ANDROID
using Android.Media;
#elif IOS || MACCATALYST
using AVFoundation;
using CoreMedia;
using Foundation;
#endif

namespace VideoLocalShow;

/// <summary>
/// Combines a video-only and an audio-only stream into a single playable file.
/// YouTube serves everything above 720p as separate video and audio streams, so this is
/// what makes it possible to actually watch a downloaded 1080p video rather than just hear it.
/// </summary>
public static class VideoMuxer
{
    public static bool IsSupported =>
#if ANDROID || IOS || MACCATALYST
        true;
#else
        false;
#endif

    /// <summary>
    /// Container formats whose streams this platform is able to read and combine.
    /// Android's MediaExtractor handles both MP4 and WebM; Apple's AVFoundation has no WebM
    /// support at all, so on iOS only MP4 streams are candidates for muxing.
    /// </summary>
    public static IReadOnlyList<string> SupportedContainers =>
#if ANDROID
        ["mp4", "webm"];
#elif IOS || MACCATALYST
        ["mp4"];
#else
        [];
#endif

    public static Task MuxAsync(string videoPath, string audioPath, string outputPath, bool useWebmContainer, CancellationToken cancellationToken)
    {
#if ANDROID
        return Task.Run(() => MuxAndroid(videoPath, audioPath, outputPath, useWebmContainer), cancellationToken);
#elif IOS || MACCATALYST
        return MuxAppleAsync(videoPath, audioPath, outputPath, cancellationToken);
#else
        throw new PlatformNotSupportedException("La combinazione di video e audio non è supportata su questa piattaforma.");
#endif
    }

#if ANDROID
    private static void MuxAndroid(string videoPath, string audioPath, string outputPath, bool useWebmContainer)
    {
        var outputType = useWebmContainer ? MuxerOutputType.Webm : MuxerOutputType.Mpeg4;

        using var videoExtractor = new MediaExtractor();
        videoExtractor.SetDataSource(videoPath);
        var videoTrackIndex = SelectTrack(videoExtractor, "video/");
        var videoFormat = videoExtractor.GetTrackFormat(videoTrackIndex)!;
        videoExtractor.SelectTrack(videoTrackIndex);

        using var audioExtractor = new MediaExtractor();
        audioExtractor.SetDataSource(audioPath);
        var audioTrackIndex = SelectTrack(audioExtractor, "audio/");
        var audioFormat = audioExtractor.GetTrackFormat(audioTrackIndex)!;
        audioExtractor.SelectTrack(audioTrackIndex);

        var muxer = new MediaMuxer(outputPath, outputType);
        try
        {
            var muxerVideoTrack = muxer.AddTrack(videoFormat);
            var muxerAudioTrack = muxer.AddTrack(audioFormat);

            muxer.Start();
            CopyTrack(videoExtractor, videoFormat, muxer, muxerVideoTrack);
            CopyTrack(audioExtractor, audioFormat, muxer, muxerAudioTrack);
            muxer.Stop();
        }
        finally
        {
            muxer.Release();
            muxer.Dispose();
        }
    }

    private static int SelectTrack(MediaExtractor extractor, string mimePrefix)
    {
        for (var i = 0; i < extractor.TrackCount; i++)
        {
            var mime = extractor.GetTrackFormat(i)?.GetString(MediaFormat.KeyMime);
            if (mime is not null && mime.StartsWith(mimePrefix, StringComparison.Ordinal))
            {
                return i;
            }
        }

        throw new InvalidOperationException($"Nessuna traccia trovata con mime type '{mimePrefix}*'.");
    }

    private static void CopyTrack(MediaExtractor extractor, MediaFormat format, MediaMuxer muxer, int muxerTrackIndex)
    {
        // Size the buffer from the track's own declared maximum sample size. A fixed guess is
        // not safe: a 1080p keyframe can easily exceed a megabyte, and a sample that doesn't
        // fit gets copied through truncated, corrupting the video track while still producing
        // a file of roughly the expected size.
        const int fallbackBufferSize = 4 * 1024 * 1024;
        var bufferSize = format.ContainsKey(MediaFormat.KeyMaxInputSize)
            ? Math.Max(format.GetInteger(MediaFormat.KeyMaxInputSize), 64 * 1024)
            : fallbackBufferSize;

        // MediaExtractor/MediaMuxer expect a direct (native-memory) NIO buffer.
        var buffer = Java.Nio.ByteBuffer.AllocateDirect(bufferSize)!;
        var bufferInfo = new MediaCodec.BufferInfo();

        while (true)
        {
            buffer.Clear();
            var sampleSize = extractor.ReadSampleData(buffer, 0);
            if (sampleSize < 0)
            {
                break;
            }

            // MediaExtractor sample flags and MediaCodec buffer flags are DIFFERENT enums whose
            // values overlap numerically, so casting one to the other mislabels samples - which
            // leaves the muxed video track undecodable even though the file looks well-formed.
            // Translate explicitly instead.
            var flags = (MediaCodecBufferFlags)0;
            if (extractor.SampleFlags.HasFlag(MediaExtractorSampleFlags.Sync))
            {
                flags |= MediaCodecBufferFlags.KeyFrame;
            }

            bufferInfo.Set(0, sampleSize, extractor.SampleTime, flags);
            muxer.WriteSampleData(muxerTrackIndex, buffer, bufferInfo);
            extractor.Advance();
        }
    }
#endif

#if IOS || MACCATALYST
    private static readonly string VideoMediaType = AVMediaTypes.Video.GetConstant()!;
    private static readonly string AudioMediaType = AVMediaTypes.Audio.GetConstant()!;

    private static async Task MuxAppleAsync(string videoPath, string audioPath, string outputPath, CancellationToken cancellationToken)
    {
        using var videoAsset = new AVUrlAsset(NSUrl.FromFilename(videoPath));
        using var audioAsset = new AVUrlAsset(NSUrl.FromFilename(audioPath));

        var videoTracks = await videoAsset.LoadTracksWithMediaTypeAsync(VideoMediaType);
        var audioTracks = await audioAsset.LoadTracksWithMediaTypeAsync(AudioMediaType);

        var sourceVideoTrack = videoTracks.Count > 0 ? videoTracks[0] : null;
        var sourceAudioTrack = audioTracks.Count > 0 ? audioTracks[0] : null;

        if (sourceVideoTrack is null)
        {
            throw new InvalidOperationException("Nessuna traccia video trovata nel flusso scaricato.");
        }

        if (sourceAudioTrack is null)
        {
            throw new InvalidOperationException("Nessuna traccia audio trovata nel flusso scaricato.");
        }

        using var composition = new AVMutableComposition();
        var compositionVideo = composition.AddMutableTrack(VideoMediaType, 0);
        var compositionAudio = composition.AddMutableTrack(AudioMediaType, 0);

        if (compositionVideo is null || compositionAudio is null)
        {
            throw new InvalidOperationException("Impossibile preparare le tracce per l'unione.");
        }

        compositionVideo.InsertTimeRange(
            new CMTimeRange { Start = CMTime.Zero, Duration = videoAsset.Duration },
            sourceVideoTrack,
            CMTime.Zero,
            out var videoError);
        if (videoError is not null)
        {
            throw new InvalidOperationException($"Unione della traccia video non riuscita: {videoError.LocalizedDescription}");
        }

        compositionAudio.InsertTimeRange(
            new CMTimeRange { Start = CMTime.Zero, Duration = audioAsset.Duration },
            sourceAudioTrack,
            CMTime.Zero,
            out var audioError);
        if (audioError is not null)
        {
            throw new InvalidOperationException($"Unione della traccia audio non riuscita: {audioError.LocalizedDescription}");
        }

        // AVAssetExportSession refuses to overwrite, so the destination must not exist yet.
        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        // Passthrough copies the existing streams as-is: no re-encoding, so the export is fast
        // and the video keeps its original quality.
        using var export = new AVAssetExportSession(composition, AVAssetExportSessionPreset.Passthrough.GetConstant()!)
        {
            OutputUrl = NSUrl.FromFilename(outputPath),
            OutputFileType = AVFileTypes.Mpeg4.GetConstant(),
            ShouldOptimizeForNetworkUse = true
        };

        using var registration = cancellationToken.Register(export.CancelExport);
        await export.ExportTaskAsync();

        if (export.Status != AVAssetExportSessionStatus.Completed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException($"Unione non riuscita: {export.Error?.LocalizedDescription ?? export.Status.ToString()}");
        }
    }
#endif
}
