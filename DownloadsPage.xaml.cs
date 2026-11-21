using System.Collections.ObjectModel;
using CommunityToolkit.Maui.Views;
using VideoLocalShow.Models;

namespace VideoLocalShow;

public partial class DownloadsPage : ContentPage
{
    // An ObservableCollection so a row dragged to a new place slides there instead of the whole
    // list being rebuilt. Its order is also the playback order.
    private ObservableCollection<DownloadedFile> _files = [];
    private bool _isSelectionMode;
    private DownloadedFile? _draggedFile;
    private int _firstVisibleIndex;
    private int _lastVisibleIndex;
    private DateTime _lastDragAutoScrollUtc;
    private DownloadedFile? _previewFile;
    private CancellationTokenSource? _rotationDebounceCts;

    public DownloadsPage()
    {
        InitializeComponent();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = LoadDownloadsAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
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

    // --- Drag and drop reordering ---------------------------------------------------------------
    // Press and hold a row, drag it over another one and let go: the dragged row takes that
    // row's place. The new order is saved, and it is the order the files play back in.

    private void OnRowDragStarting(object? sender, DragStartingEventArgs e)
    {
        _draggedFile = (sender as DragGestureRecognizer)?.Parent?.BindingContext as DownloadedFile;
        if (_draggedFile is null)
        {
            e.Cancel = true;
            return;
        }

        // The dragged row is tracked in _draggedFile; the payload only has to exist, since some
        // platforms refuse to start a drag with an empty one.
        e.Data.Text = _draggedFile.FileName;
    }

    private void OnRowDropCompleted(object? sender, DropCompletedEventArgs e) => _draggedFile = null;

    private void OnRowDragOver(object? sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
        ScrollListWhileDragging(e);
    }

    private void OnRowDrop(object? sender, DropEventArgs e)
    {
        var target = (sender as DropGestureRecognizer)?.Parent?.BindingContext as DownloadedFile;
        var dragged = _draggedFile;
        _draggedFile = null;

        if (dragged is null || target is null || ReferenceEquals(dragged, target))
        {
            return;
        }

        var from = _files.IndexOf(dragged);
        var to = _files.IndexOf(target);
        if (from < 0 || to < 0)
        {
            return;
        }

        _files.Move(from, to);
        DownloadOrderStore.Save(_files.Select(f => f.FileName));
    }

    private void OnDownloadsScrolled(object? sender, ItemsViewScrolledEventArgs e)
    {
        _firstVisibleIndex = e.FirstVisibleItemIndex;
        _lastVisibleIndex = e.LastVisibleItemIndex;
    }

    // The list doesn't scroll by itself while a row is being dragged, so without this a row could
    // only be moved as far as the screen currently shows. Holding it near the top or bottom edge
    // scrolls the list a row at a time, throttled so it moves at a followable speed.
    private void ScrollListWhileDragging(DragEventArgs e)
    {
        const double edgeZone = 90;
        const int minMillisecondsBetweenScrolls = 350;

        if (e.GetPosition(DownloadsRefreshView) is not { } position ||
            (DateTime.UtcNow - _lastDragAutoScrollUtc).TotalMilliseconds < minMillisecondsBetweenScrolls)
        {
            return;
        }

        if (position.Y < edgeZone && _firstVisibleIndex > 0)
        {
            _lastDragAutoScrollUtc = DateTime.UtcNow;
            DownloadsList.ScrollTo(_firstVisibleIndex - 1, position: ScrollToPosition.MakeVisible, animate: true);
        }
        else if (position.Y > DownloadsRefreshView.Height - edgeZone && _lastVisibleIndex < _files.Count - 1)
        {
            _lastDragAutoScrollUtc = DateTime.UtcNow;
            DownloadsList.ScrollTo(_lastVisibleIndex + 1, position: ScrollToPosition.MakeVisible, animate: true);
        }
    }

    /// <summary>
    /// Toggles between the normal per-row play/delete controls and a multi-select mode where
    /// each row shows a checkbox instead, for picking several files to delete at once.
    /// </summary>
    private void OnToggleSelectionModeClicked(object? sender, EventArgs e)
    {
        _isSelectionMode = !_isSelectionMode;
        SelectionToolbarItem.Text = _isSelectionMode ? "Annulla" : "Seleziona";

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
