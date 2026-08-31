using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace VideoLocalShow.Models;

public class SearchResultOption : INotifyPropertyChanged
{
    private bool _isResolving;
    private bool _isDownloading;
    private bool _isDownloadComplete;
    private double _downloadProgress;
    private ICommand? _stopDownloadCommand;

    public required string VideoId { get; init; }

    public required string Title { get; init; }

    public required string ChannelTitle { get; init; }

    public required string DurationText { get; init; }

    public string? ThumbnailUrl { get; init; }

    /// <summary>Matched by title against the Downloads folder, so it's known without any
    /// extra network call.</summary>
    public bool IsAlreadyDownloaded { get; init; }

    /// <summary>Tapping the row's download icon resolves and downloads this video immediately -
    /// there is no selection step first, one tap is the whole action.</summary>
    public required ICommand DownloadCommand { get; init; }

    /// <summary>
    /// Set the instant the download icon is tapped, before the video is even resolved -
    /// resolving (fetching video info and stream manifest from YouTube) can take a couple of
    /// seconds, and hiding the icon immediately stops an impatient extra tap from queuing a
    /// second, redundant download of the same video.
    /// </summary>
    public bool IsResolving
    {
        get => _isResolving;
        set
        {
            if (_isResolving == value)
            {
                return;
            }

            _isResolving = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowDownloadButton));
        }
    }

    /// <summary>
    /// While this row is downloading, the download icon is replaced by a small stop button and
    /// a progress bar right here in the row - there is no separate list to look at elsewhere for it.
    /// </summary>
    public bool IsDownloading
    {
        get => _isDownloading;
        set
        {
            if (_isDownloading == value)
            {
                return;
            }

            _isDownloading = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowDownloadButton));
        }
    }

    /// <summary>
    /// Set once a download started from this row finishes successfully, so the row keeps
    /// showing a checkmark in place of the download icon instead of reverting to it.
    /// </summary>
    public bool IsDownloadComplete
    {
        get => _isDownloadComplete;
        set
        {
            if (_isDownloadComplete == value)
            {
                return;
            }

            _isDownloadComplete = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowDownloadButton));
        }
    }

    /// <summary>The download icon only makes sense while nothing is happening yet - hide it
    /// once the video is resolving or downloading (replaced by a spinner, then the stop button),
    /// already finished this session, or already sitting in the Downloads folder from an earlier
    /// one (replaced by the checkmark either way) - there is no reason to let a tap queue a
    /// second, redundant copy of a video that's already there.</summary>
    public bool ShowDownloadButton => !IsResolving && !IsDownloading && !IsDownloadComplete && !IsAlreadyDownloaded;

    /// <summary>Shown in the download icon's place once there is nothing left to download -
    /// either because this row's own download just finished, or because it was already sitting
    /// in the Downloads folder before the search even ran.</summary>
    public bool ShowCompleteCheckmark => IsDownloadComplete || IsAlreadyDownloaded;

    public double DownloadProgress
    {
        get => _downloadProgress;
        set
        {
            if (_downloadProgress == value)
            {
                return;
            }

            _downloadProgress = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Assigned only once the download actually starts, well after this row is already on
    /// screen with its Stop button bound - a plain auto-property here would leave that binding
    /// stuck on the initial null value, since there'd be no change notification to tell it a
    /// real command is now available.
    /// </summary>
    public ICommand? StopDownloadCommand
    {
        get => _stopDownloadCommand;
        set
        {
            if (_stopDownloadCommand == value)
            {
                return;
            }

            _stopDownloadCommand = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
