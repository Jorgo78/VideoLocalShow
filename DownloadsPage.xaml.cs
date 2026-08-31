using CommunityToolkit.Maui.Views;
using VideoLocalShow.Models;

namespace VideoLocalShow;

public partial class DownloadsPage : ContentPage
{
    private List<DownloadedFile> _files = [];
    private bool _isSelectionMode;
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
        var files = await Task.Run(() => Directory.EnumerateFiles(folder)
            .Select(path => new FileInfo(path))
            .OrderByDescending(info => info.LastWriteTimeUtc)
            .Select(info => new DownloadedFile
            {
                FilePath = info.FullName,
                FileName = info.Name,
                Details = $"{FormatSize(info.Length)} · {info.LastWriteTime:g}",
                PlayCommand = new Command<DownloadedFile>(async f => await PlayAsync(f!)),
                IsSelectionMode = _isSelectionMode
            })
            .ToList());

        _files = files;
        DownloadsList.ItemsSource = _files;
        EmptyLabel.IsVisible = _files.Count == 0;
        UpdateDeleteSelectedButton();

        _ = LoadThumbnailsAsync(_files);
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

    private static async Task LoadThumbnailsAsync(List<DownloadedFile> files)
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
    private Task PlayAsync(DownloadedFile file)
    {
        _previewFile = file;
        PreviewTitleLabel.Text = file.FileName;

        // Order matters here: the player's native surface only binds correctly once it's part
        // of the visible tree, so IsVisible has to flip on before Source is assigned - the other
        // way around, Android happily plays the audio track with no video surface to render
        // into, and the preview looks "stuck" on black.
        PreviewContainer.IsVisible = true;
        PreviewPlayer.Source = MediaSource.FromFile(file.FilePath);

        DeviceDisplay.Current.MainDisplayInfoChanged += OnDisplayInfoChanged;
        return Task.CompletedTask;
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
