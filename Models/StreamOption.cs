using YoutubeExplode.Videos.Streams;

namespace VideoLocalShow.Models;

/// <summary>
/// The single best video+audio combination available for a video. There is no format picker
/// in the UI, so this only needs to carry what a download actually needs.
/// </summary>
public class StreamOption
{
    public required IStreamInfo Stream { get; init; }

    /// <summary>
    /// When set, <see cref="Stream"/> is a video-only stream that must be muxed together
    /// with this separate audio-only stream to produce a playable video+audio file.
    /// </summary>
    public IStreamInfo? AudioStreamForMuxing { get; init; }

    public required string FileExtension { get; init; }
}
