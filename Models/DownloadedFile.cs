using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace VideoLocalShow.Models;

public class DownloadedFile : INotifyPropertyChanged
{
    private string? _thumbnailPath;
    private bool _isSelected;
    private bool _isSelectionMode;
    private bool _isNowPlaying;
    private bool _isDragging;

    public required string FilePath { get; init; }

    public required string FileName { get; init; }

    public required string Details { get; init; }

    public required ICommand PlayCommand { get; init; }


    /// <summary>True while this row is picked up for drag-and-drop reordering, so it can be tinted
    /// until it is let go.</summary>
    public bool IsDragging
    {
        get => _isDragging;
        set
        {
            if (_isDragging == value)
            {
                return;
            }

            _isDragging = value;
            OnPropertyChanged();
        }
    }

    /// <summary>True for the row currently playing in the preview, so the list shows where the
    /// sequence has got to.</summary>
    public bool IsNowPlaying
    {
        get => _isNowPlaying;
        set
        {
            if (_isNowPlaying == value)
            {
                return;
            }

            _isNowPlaying = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// True while the page's multi-select mode is on. Set on every row at once (by the page,
    /// not per-row) so the checkbox and the individual play/delete buttons can swap places via
    /// binding instead of the page reaching into each row's visuals directly.
    /// </summary>
    public bool IsSelectionMode
    {
        get => _isSelectionMode;
        set
        {
            if (_isSelectionMode == value)
            {
                return;
            }

            _isSelectionMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowRowActions));
        }
    }

    /// <summary>The per-row play button only makes sense outside selection mode - while
    /// selecting, the checkbox takes its place.</summary>
    public bool ShowRowActions => !IsSelectionMode;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Filled in after the list is already on screen: decoding a frame takes long enough that
    /// waiting for every thumbnail before showing anything would leave the page blank.
    /// </summary>
    public string? ThumbnailPath
    {
        get => _thumbnailPath;
        set
        {
            if (_thumbnailPath == value)
            {
                return;
            }

            _thumbnailPath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasThumbnail));
            OnPropertyChanged(nameof(HasNoThumbnail));
        }
    }

    public bool HasThumbnail => !string.IsNullOrEmpty(_thumbnailPath);

    /// <summary>Exposed so the placeholder can bind directly, without a value converter.</summary>
    public bool HasNoThumbnail => !HasThumbnail;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
