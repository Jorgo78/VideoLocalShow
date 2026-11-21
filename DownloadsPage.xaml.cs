using System.Collections.ObjectModel;
using CommunityToolkit.Maui.Views;
using VideoLocalShow.Models;

namespace VideoLocalShow;

public partial class DownloadsPage : ContentPage
{
    // An ObservableCollection: the CollectionView's own drag-to-reorder moves items inside the
    // bound collection, so it has to be one it can modify. Its order is also the playback order.
    private ObservableCollection<DownloadedFile> _files = [];
    private bool _isSelectionMode;
    private DownloadedFile? _draggingFile;
    private bool _dragTrackerAttached;
    private DownloadedFile? _previewFile;
    private CancellationTokenSource? _rotationDebounceCts;

    public DownloadsPage()
    {
        InitializeComponent();
        DownloadsList.HandlerChanged += OnDownloadsListHandlerChanged;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
#if ANDROID
        RowDragTracker.TouchEnded += OnTouchEnded;
#endif
        _ = LoadDownloadsAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
#if ANDROID
        RowDragTracker.TouchEnded -= OnTouchEnded;
#endif
        SetDraggingRow(null);
        // Leaving the page (switching tabs) should never leave this inline preview quietly
        // playing audio in the background.
        ClosePreview();
    }

    private async void OnRefreshing(object? sender, EventArgs e)
    {
        await LoadDownloadsAsync();
        DownloadsRefreshView.IsRefreshing = false;
    }

    private async Task LoadDownloadsAsync()
    {
        var folder = DownloadStorage.GetFolder();

        // Enumerating the folder, stat-ing every file for its size and last-write time, and
        // sorting by that all touch disk - with a lot of downloaded files this was running
        // straight on the UI thread, so opening this tab froze for however long that scan took,
        // worse the more files there were to look at. A background thread keeps the tab
        // responsive regardless of how large the folder has grown.
        var files = await Task.Run(() => DownloadOrderStore.Apply(
                Directory.EnumerateFiles(folder).Select(path => new FileInfo(path)),
                info => info.Name,
                info => info.LastWriteTimeUtc)
            .Select(info => new DownloadedFile
            {
                FilePath = info.FullName,
                FileName = info.Name,
                Details = $"{FormatSize(info.Length)} · {info.LastWriteTime:g}",
                PlayCommand = new Command<DownloadedFile>(async f => await PlayAsync(f!)),
                IsSelectionMode = _isSelectionMode
            })
            .ToList());

        _files = new ObservableCollection<DownloadedFile>(files);
        DownloadsList.ItemsSource = _files;
        EmptyLabel.IsVisible = _files.Count == 0;
        UpdateDeleteSelectedButton();

        _ = LoadThumbnailsAsync(files);
    }

    // Fires once a row has been dragged to its new place (the CollectionView has already moved
    // it in _files by then): save the order, which is also the order the files play back in.
    private void OnReorderCompleted(object? sender, EventArgs e)
    {
        SetDraggingRow(null);
        DownloadOrderStore.Save(_files.Select(f => f.FileName));
    }

    // --- Highlight of the row being dragged -------------------------------------------------------
    // The CollectionView's own reorder tells us nothing about a drag starting, so each platform
    // watches for the same long press that starts it (and for the finger lifting again) and reports
    // the row here; the row's tint is bound to DownloadedFile.IsDragging.

    private void SetDraggingRow(DownloadedFile? file)
    {
        if (_draggingFile is not null)
        {
            _draggingFile.IsDragging = false;
        }

        _draggingFile = file;
        if (file is not null)
        {
            file.IsDragging = true;
        }
    }

    private void OnDownloadsListHandlerChanged(object? sender, EventArgs e)
    {
        if (_dragTrackerAttached)
        {
            return;
        }

#if ANDROID
        if (DownloadsList.Handler?.PlatformView is AndroidX.RecyclerView.Widget.RecyclerView recyclerView)
        {
            recyclerView.AddOnItemTouchListener(new RowDragTracker(recyclerView.Context!, item =>
            {
                // While ticking rows for deletion nothing is draggable, so nothing is highlighted either.
                if (!_isSelectionMode)
                {
                    SetDraggingRow(item as DownloadedFile);
                }
            }));
            _dragTrackerAttached = true;
        }
#elif IOS
        if (DownloadsList.Handler?.PlatformView is UIKit.UICollectionView collectionView)
        {
            // A second long-press recognizer alongside the one MAUI uses for the reorder itself;
            // it must be allowed to recognize at the same time as it, and must not swallow touches.
            collectionView.AddGestureRecognizer(new UIKit.UILongPressGestureRecognizer(
                recognizer => OnRowLongPressChanged(recognizer, collectionView))
            {
                CancelsTouchesInView = false,
                Delegate = new SimultaneousRecognitionDelegate()
            });
            _dragTrackerAttached = true;
        }
#endif
    }

#if ANDROID
    private void OnTouchEnded() => MainThread.BeginInvokeOnMainThread(() => SetDraggingRow(null));
#elif IOS
    private void OnRowLongPressChanged(UIKit.UILongPressGestureRecognizer recognizer, UIKit.UICollectionView collectionView)
    {
        switch (recognizer.State)
        {
            case UIKit.UIGestureRecognizerState.Began:
                var indexPath = collectionView.IndexPathForItemAtPoint(recognizer.LocationInView(collectionView));
                if (!_isSelectionMode && indexPath is not null && (int)indexPath.Item < _files.Count)
                {
                    SetDraggingRow(_files[(int)indexPath.Item]);
                }

                break;

            case UIKit.UIGestureRecognizerState.Ended:
            case UIKit.UIGestureRecognizerState.Cancelled:
            case UIKit.UIGestureRecognizerState.Failed:
                SetDraggingRow(null);
                break;
        }
    }

    private sealed class SimultaneousRecognitionDelegate : UIKit.UIGestureRecognizerDelegate
    {
        public override bool ShouldRecognizeSimultaneously(UIKit.UIGestureRecognizer gestureRecognizer, UIKit.UIGestureRecognizer otherGestureRecognizer) => true;
    }
#endif

    /// <summary>
    /// Toggles between the normal per-row play/delete controls and a multi-select mode where
    /// each row shows a checkbox instead, for picking several files to delete at once.
    /// </summary>
    private void OnToggleSelectionModeClicked(object? sender, EventArgs e)
    {
        _isSelectionMode = !_isSelectionMode;
        SelectionToolbarItem.Text = _isSelectionMode ? "Annulla" : "Seleziona";

        // While ticking rows for deletion, a press-and-hold must not pick a row up.
        DownloadsList.CanReorderItems = !_isSelectionMode;

        foreach (var file in _files)
        {
            file.IsSelectionMode = _isSelectionMode;
            if (!_isSelectionMode)
            {
                file.IsSelected = false;
            }
        }

        UpdateDeleteSelectedButton();
    }

    private void OnDownloadCheckedChanged(object? sender, CheckedChangedEventArgs e) => UpdateDeleteSelectedButton();

    private void UpdateDeleteSelectedButton()
    {
        var count = _files.Count(f => f.IsSelected);
        DeleteSelectedButton.IsVisible = _isSelectionMode;
        DeleteSelectedButton.IsEnabled = count > 0;
        DeleteSelectedButton.Text = count > 0 ? $"Elimina selezionati ({count})" : "Elimina selezionati";
    }

    private async void OnDeleteSelectedClicked(object? sender, EventArgs e)
    {
        var selected = _files.Where(f => f.IsSelected).ToList();
        if (selected.Count == 0)
        {
            return;
        }

#pragma warning disable CS0618 // see DeleteAsync below for why DisplayAlert is used over DisplayAlertAsync
        var confirmed = await DisplayAlert(
            "Eliminare i file?",
            selected.Count == 1 ? selected[0].FileName : $"{selected.Count} file selezionati",
            "Elimina",
            "Annulla");
#pragma warning restore CS0618
        if (!confirmed)
        {
            return;
        }

        foreach (var file in selected)
        {
            File.Delete(file.FilePath);
        }

        _isSelectionMode = false;
        SelectionToolbarItem.Text = "Seleziona";
        DownloadsList.CanReorderItems = true;
        await LoadDownloadsAsync();
    }

    // Run a handful at a time rather than one file after another - GetThumbnailAsync's cache-hit
    // path (an already-generated thumbnail, the common case on every load after the first) is
    // cheap disk I/O that benefits from overlapping, and even the slow first-time path - actually
    // decoding a video frame - is worth spreading across a couple of cores instead of making
    // every later file wait for every earlier one to finish first. Left unbounded, a folder with
    // a lot of downloads would fire off just as many decodes at once and thrash instead of help.
    private const int MaxConcurrentThumbnailLoads = 4;

    private static async Task LoadThumbnailsAsync(IReadOnlyList<DownloadedFile> files)
    {
        using var throttle = new SemaphoreSlim(MaxConcurrentThumbnailLoads);

        await Task.WhenAll(files.Select(async file =>
        {
            await throttle.WaitAsync();
            try
            {
                var thumbnailPath = await ThumbnailProvider.GetThumbnailAsync(file.FilePath);
                if (thumbnailPath is not null)
                {
                    MainThread.BeginInvokeOnMainThread(() => file.ThumbnailPath = thumbnailPath);
                }
            }
            finally
            {
                throttle.Release();
            }
        }));
    }

    // Tapping a row's play button opens this inline preview instead of jumping straight to the
    // full player - a quick look without leaving the list. Rotating the device to landscape
    // while it's open expands it in place to fill the page - see SetPreviewFullscreen - and
    // rotating back to portrait shrinks it back down, without ever navigating to another page.
    //
    // Playback runs through the files in list order: when one ends, the next one in the list
    // starts (see OnPreviewMediaEnded), and the previous/next buttons in the preview's header
    // jump along the same list. The list order is whatever the user arranged in reorder mode.
    private Task PlayAsync(DownloadedFile file)
    {
        StartPlayback(file);
        return Task.CompletedTask;
    }

    private void StartPlayback(DownloadedFile file)
    {
        if (_previewFile is not null)
        {
            _previewFile.IsNowPlaying = false;
        }

        _previewFile = file;
        file.IsNowPlaying = true;
        PreviewTitleLabel.Text = file.FileName;

        // Shown on the lock screen / notification (and by the headset controls) while playing in
        // the background.
        PreviewPlayer.MetadataTitle = Path.GetFileNameWithoutExtension(file.FileName);
        PreviewPlayer.MetadataArtist = "VideoLocalShow";

        // Only the first file of a run needs the rotation hook and the permission prompt -
        // moving on to the next file leaves the preview open as it was.
        var isFirstOfRun = !PreviewContainer.IsVisible;

        // Order matters here: the player's native surface only binds correctly once it's part
        // of the visible tree, so IsVisible has to flip on before Source is assigned - the other
        // way around, Android happily plays the audio track with no video surface to render
        // into, and the preview looks "stuck" on black.
        PreviewContainer.IsVisible = true;
        PreviewPlayer.Source = MediaSource.FromFile(file.FilePath);

        if (isFirstOfRun)
        {
            DeviceDisplay.Current.MainDisplayInfoChanged += OnDisplayInfoChanged;
#if ANDROID
            _ = EnsureNotificationPermissionAsync();
#endif
        }
    }

#if ANDROID
    // Android 13+ shows the media-playback notification (the thing that keeps playback alive in
    // the background and carries its controls) only once this permission is granted. Playback
    // itself works without it, so a refusal is not an error.
    private static async Task EnsureNotificationPermissionAsync()
    {
        try
        {
            if (await Permissions.CheckStatusAsync<Permissions.PostNotifications>() != PermissionStatus.Granted)
            {
                await Permissions.RequestAsync<Permissions.PostNotifications>();
            }
        }
        catch (Exception)
        {
            // Best effort only.
        }
    }
#endif

    private void OnPlayAllClicked(object? sender, EventArgs e)
    {
        if (_files.Count == 0)
        {
            return;
        }

        StartPlayback(_files[0]);
    }

    private void OnPreviewPreviousClicked(object? sender, EventArgs e) => PlayRelative(-1);

    private void OnPreviewNextClicked(object? sender, EventArgs e) => PlayRelative(+1);

    // Fires when the current file plays to its end. MediaEnded may arrive on a background thread.
    private void OnPreviewMediaEnded(object? sender, EventArgs e) =>
        MainThread.BeginInvokeOnMainThread(() => PlayRelative(+1, closeAtEnd: true));

    private void PlayRelative(int offset, bool closeAtEnd = false)
    {
        if (_previewFile is null)
        {
            return;
        }

        var index = IndexOfPreviewFile();
        var target = index + offset;
        if (index < 0 || target < 0 || target >= _files.Count)
        {
            // Past the last file at the end of the sequence: nothing left to play.
            if (closeAtEnd)
            {
                ClosePreview();
            }

            return;
        }

        StartPlayback(_files[target]);
    }

    // By path rather than by object reference: pulling to refresh rebuilds every row while the
    // preview keeps playing, so the file being played may not be one of the current row objects.
    private int IndexOfPreviewFile()
    {
        for (var i = 0; i < _files.Count; i++)
        {
            if (_files[i].FilePath == _previewFile?.FilePath)
            {
                return i;
            }
        }

        return -1;
    }

    private void OnPreviewCloseClicked(object? sender, EventArgs e) => ClosePreview();

    // Android fires MainDisplayInfoChanged several times in quick succession while a rotation
    // animation is in flight, sometimes with a stale/intermediate orientation reading before the
    // final one - reacting to every event as it arrives could flip fullscreen on, then back off,
    // depending on which reading happens to land last. Debouncing to the value that's still
    // current a moment after events stop arriving avoids acting on those transient readings.
    private void OnDisplayInfoChanged(object? sender, DisplayInfoChangedEventArgs e)
    {
        _rotationDebounceCts?.Cancel();
        var cts = new CancellationTokenSource();
        _rotationDebounceCts = cts;
        var isLandscape = e.DisplayInfo.Orientation == DisplayOrientation.Landscape;

        _ = DebounceRotationAsync(isLandscape, cts.Token);
    }

    private async Task DebounceRotationAsync(bool isLandscape, CancellationToken token)
    {
        try
        {
            await Task.Delay(250, token);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        if (token.IsCancellationRequested)
        {
            return;
        }

        MainThread.BeginInvokeOnMainThread(() => SetPreviewFullscreen(isLandscape));
    }

    // Expands the preview across every row of the page's own Grid (rather than just its normal
    // Row="0" slot) instead of navigating to a different page - the video, and the rest of the
    // page underneath it, never stop being exactly where they were.
    private void SetPreviewFullscreen(bool fullscreen)
    {
        Grid.SetRowSpan(PreviewContainer, fullscreen ? 3 : 1);
        PreviewContainer.HeightRequest = fullscreen ? -1 : 210;
        PreviewContainer.Margin = fullscreen ? new Thickness(0) : new Thickness(0, 0, 0, 12);
        Shell.SetNavBarIsVisible(this, !fullscreen);
        Shell.SetTabBarIsVisible(this, !fullscreen);
    }

    private void ClosePreview()
    {
        if (!PreviewContainer.IsVisible)
        {
            return;
        }

        DeviceDisplay.Current.MainDisplayInfoChanged -= OnDisplayInfoChanged;
        _rotationDebounceCts?.Cancel();
        SetPreviewFullscreen(false);
        PreviewPlayer.Stop();
        PreviewContainer.IsVisible = false;
        PreviewPlayer.Source = null;
        if (_previewFile is not null)
        {
            _previewFile.IsNowPlaying = false;
        }

        _previewFile = null;
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double size = bytes;
        var unitIndex = 0;
        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return $"{size:0.#} {units[unitIndex]}";
    }
}
