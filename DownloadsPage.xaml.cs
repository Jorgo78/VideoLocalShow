using System.Collections.ObjectModel;
using CommunityToolkit.Maui.Views;
using VideoLocalShow.Models;

namespace VideoLocalShow;

public partial class DownloadsPage : ContentPage
{
    // The whole arrangement in order: group headings (GroupHeader) and videos (DownloadedFile). A
    // video belongs to the nearest heading above it; those above the first heading are in no group.
    private List<object> _master = [];

    // Every video in _master's order - hidden ones in collapsed groups included. This is the
    // playback order.
    private List<DownloadedFile> _files = [];

    // What the list actually shows: _master without the videos of collapsed groups. An
    // ObservableCollection because the CollectionView's own drag-to-reorder moves items inside the
    // bound collection.
    private ObservableCollection<object> _rows = [];
    private bool _isSelectionMode;
    private DownloadedFile? _draggingFile;
    private Thickness _normalRootPadding;
    private SafeAreaEdges _normalSafeAreaEdges;
#if IOS
    private bool _normalUsesSafeArea;
#endif
    private bool _dragTrackerAttached;
    private DownloadedFile? _previewFile;
    private CancellationTokenSource? _rotationDebounceCts;

    public DownloadsPage()
    {
        InitializeComponent();

        // What the page looks like outside full screen, to put back afterwards.
        _normalRootPadding = RootGrid.Padding;
        _normalSafeAreaEdges = RootGrid.SafeAreaEdges;
#if IOS
        _normalUsesSafeArea = Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific.Page.GetUseSafeArea(this);
#endif

        DownloadsList.HandlerChanged += OnDownloadsListHandlerChanged;
        SizeChanged += OnPageSizeChanged;
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
        // arranging them all touch disk - with a lot of downloaded files this was running
        // straight on the UI thread, so opening this tab froze for however long that scan took,
        // worse the more files there were to look at. A background thread keeps the tab
        // responsive regardless of how large the folder has grown.
        var master = await Task.Run(() =>
        {
            var infos = Directory.EnumerateFiles(folder).Select(path => new FileInfo(path)).ToList();
            var byName = infos.ToDictionary(
                info => info.Name,
                info => new DownloadedFile
                {
                    FilePath = info.FullName,
                    FileName = info.Name,
                    Details = $"{FormatSize(info.Length)} · {info.LastWriteTime:g}",
                    PlayCommand = new Command<DownloadedFile>(async f => await PlayAsync(f!)),
                    IsSelectionMode = _isSelectionMode
                },
                StringComparer.Ordinal);
            var written = infos.ToDictionary(info => info.Name, info => info.LastWriteTimeUtc, StringComparer.Ordinal);

            var layout = LibraryLayoutStore.Load();
            var placed = layout.Where(e => e.File is not null).Select(e => e.File!).ToHashSet(StringComparer.Ordinal);

            // Files the user hasn't placed yet - freshly downloaded ones - go first, newest first,
            // above any group, so a new download is visible at the top rather than buried.
            var result = new List<object>();
            result.AddRange(byName.Values
                .Where(f => !placed.Contains(f.FileName))
                .OrderByDescending(f => written[f.FileName]));

            var seenGroups = new HashSet<string>();
            var seenFiles = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in layout)
            {
                if (entry.File is not null)
                {
                    if (byName.TryGetValue(entry.File, out var file) && seenFiles.Add(entry.File))
                    {
                        result.Add(file);
                    }
                }
                else if (entry.GroupId is not null && seenGroups.Add(entry.GroupId))
                {
                    result.Add(CreateGroup(entry.GroupId, entry.GroupName ?? "Gruppo", entry.Expanded));
                }
            }

            foreach (var file in byName.Values)
            {
                file.MenuCommand = new Command<DownloadedFile>(async f => await ShowFileMenuAsync(f!));
            }

            return result;
        });

        _master = master;
        RefreshStructure();
        RebuildRows();
        UpdateDeleteSelectedButton();

        _ = LoadThumbnailsAsync(_files);
    }

    private GroupHeader CreateGroup(string id, string name, bool expanded)
    {
        var group = new GroupHeader(id, name, expanded);
        group.ToggleCommand = new Command(() => ToggleGroup(group));
        group.MenuCommand = new Command(async () => await ShowGroupMenuAsync(group));
        return group;
    }

    // --- Groups ---------------------------------------------------------------------------------

    // After any change to _master: which group each video is in (for its indent), how many videos
    // each group holds, the flat list of videos, and the saved layout.
    private void RefreshStructure()
    {
        GroupHeader? current = null;
        var count = 0;
        foreach (var item in _master)
        {
            if (item is GroupHeader header)
            {
                if (current is not null)
                {
                    current.Count = count;
                }

                current = header;
                count = 0;
            }
            else if (item is DownloadedFile file)
            {
                file.IsInGroup = current is not null;
                count++;
            }
        }

        if (current is not null)
        {
            current.Count = count;
        }

        _files = _master.OfType<DownloadedFile>().ToList();
        EmptyLabel.IsVisible = _files.Count == 0 && !_master.OfType<GroupHeader>().Any();
        SaveLayout();
    }

    private void SaveLayout() =>
        LibraryLayoutStore.Save(_master.Select(item => item is GroupHeader group
            ? new LayoutEntry { GroupId = group.Id, GroupName = group.Name, Expanded = group.IsExpanded }
            : new LayoutEntry { File = ((DownloadedFile)item).FileName }));

    // Shows _master minus the videos of collapsed groups.
    private void RebuildRows()
    {
        var rows = new List<object>();
        var hidden = false;
        foreach (var item in _master)
        {
            if (item is GroupHeader header)
            {
                hidden = !header.IsExpanded;
                rows.Add(header);
            }
            else if (!hidden)
            {
                rows.Add(item);
            }
        }

        _rows = new ObservableCollection<object>(rows);
        DownloadsList.ItemsSource = _rows;
    }

    private List<DownloadedFile> MembersOf(GroupHeader group)
    {
        var members = new List<DownloadedFile>();
        var index = _master.IndexOf(group);
        for (var i = index + 1; index >= 0 && i < _master.Count && _master[i] is DownloadedFile file; i++)
        {
            members.Add(file);
        }

        return members;
    }

    private void ToggleGroup(GroupHeader group)
    {
        group.IsExpanded = !group.IsExpanded;
        var members = MembersOf(group);
        var headerRow = _rows.IndexOf(group);
        if (group.IsExpanded)
        {
            for (var i = 0; i < members.Count; i++)
            {
                _rows.Insert(headerRow + 1 + i, members[i]);
            }
        }
        else
        {
            foreach (var member in members)
            {
                _rows.Remove(member);
            }
        }

        SaveLayout();
    }

    private async void OnNewGroupClicked(object? sender, EventArgs e)
    {
        var name = await AskGroupNameAsync("Nuovo gruppo", null);
        if (name is null)
        {
            return;
        }

        // At the end: a heading at the top would swallow every video below it.
        _master.Add(CreateGroup(Guid.NewGuid().ToString("N"), name, expanded: true));
        RefreshStructure();
        RebuildRows();
    }

    private async Task<string?> AskGroupNameAsync(string title, string? current)
    {
        var name = await DisplayPromptAsync(title, "Nome del gruppo", "OK", "Annulla", initialValue: current ?? string.Empty, maxLength: 60);
        return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    // The three dots of a video row: pick the group to move it to (or take it out of its group).
    private async Task ShowFileMenuAsync(DownloadedFile file)
    {
        var groups = _master.OfType<GroupHeader>().ToList();
        var current = GroupOf(file);
        var targets = groups.Where(g => g != current).ToList();

        const string newGroup = "＋ Nuovo gruppo…";
        const string noGroup = "Fuori da ogni gruppo";
        var options = new List<string>();
        options.AddRange(targets.Select(g => g.Name));
        options.Add(newGroup);
        if (current is not null)
        {
            options.Add(noGroup);
        }

#pragma warning disable CS0618 // see DeleteAsync below for why the non-Async alert methods are used
        var choice = await DisplayActionSheet("Sposta in…", "Annulla", null, options.ToArray());
#pragma warning restore CS0618
        if (choice is null || choice == "Annulla")
        {
            return;
        }

        if (choice == newGroup)
        {
            var name = await AskGroupNameAsync("Nuovo gruppo", null);
            if (name is null)
            {
                return;
            }

            var created = CreateGroup(Guid.NewGuid().ToString("N"), name, expanded: true);
            _master.Add(created);
            MoveFileToGroup(file, created);
        }
        else if (choice == noGroup)
        {
            MoveFileToGroup(file, null);
        }
        else if (targets.FirstOrDefault(g => g.Name == choice) is { } target)
        {
            MoveFileToGroup(file, target);
        }
    }

    private GroupHeader? GroupOf(DownloadedFile file)
    {
        GroupHeader? current = null;
        foreach (var item in _master)
        {
            if (item is GroupHeader header)
            {
                current = header;
            }
            else if (ReferenceEquals(item, file))
            {
                return current;
            }
        }

        return null;
    }

    // To the end of the group, or - for null - to the end of the videos that sit in no group.
    private void MoveFileToGroup(DownloadedFile file, GroupHeader? group)
    {
        _master.Remove(file);

        int insertAt;
        if (group is null)
        {
            insertAt = _master.FindIndex(item => item is GroupHeader);
            if (insertAt < 0)
            {
                insertAt = _master.Count;
            }
        }
        else
        {
            insertAt = _master.IndexOf(group) + 1;
            while (insertAt < _master.Count && _master[insertAt] is DownloadedFile)
            {
                insertAt++;
            }

            // Moved into a collapsed group it would vanish from view.
            group.IsExpanded = true;
        }

        _master.Insert(insertAt, file);
        RefreshStructure();
        RebuildRows();
    }

    private async Task ShowGroupMenuAsync(GroupHeader group)
    {
        const string play = "Riproduci il gruppo";
        const string rename = "Rinomina";
        const string delete = "Elimina il gruppo (i video restano)";

        var options = new List<string>();
        if (MembersOf(group).Count > 0)
        {
            options.Add(play);
        }

        options.Add(rename);
        options.Add(delete);

#pragma warning disable CS0618 // see DeleteAsync below for why the non-Async alert methods are used
        var choice = await DisplayActionSheet(group.Name, "Annulla", null, options.ToArray());
#pragma warning restore CS0618

        if (choice == play)
        {
            if (MembersOf(group).FirstOrDefault() is { } first)
            {
                StartPlayback(first);
            }
        }
        else if (choice == rename)
        {
            var name = await AskGroupNameAsync("Rinomina il gruppo", group.Name);
            if (name is not null)
            {
                group.Name = name;
                SaveLayout();
            }
        }
        else if (choice == delete)
        {
            DeleteGroup(group);
        }
    }

    // The videos of a deleted group would fall into the group above it, so they are moved out to
    // the ungrouped ones at the top first.
    private void DeleteGroup(GroupHeader group)
    {
        var members = MembersOf(group);
        _master.Remove(group);
        foreach (var member in members)
        {
            _master.Remove(member);
        }

        var insertAt = _master.FindIndex(item => item is GroupHeader);
        if (insertAt < 0)
        {
            insertAt = _master.Count;
        }

        _master.InsertRange(insertAt, members);
        RefreshStructure();
        RebuildRows();
    }

    // Fires once a row has been dragged to its new place (the CollectionView has already moved
    // it in _rows by then). Folds the new position back into _master - a video dropped under a
    // heading is now in that group - and saves it. Dragging a heading past another heading would
    // reshuffle the groups, which isn't what that gesture is for, so it snaps back instead.
    private void OnReorderCompleted(object? sender, EventArgs e)
    {
        SetDraggingRow(null);

        var oldHeaders = _master.OfType<GroupHeader>().ToList();
        if (!oldHeaders.SequenceEqual(_rows.OfType<GroupHeader>()))
        {
            MainThread.BeginInvokeOnMainThread(RebuildRows);
            return;
        }

        // A video can be dropped in its own group or in any other group, but one taken out of a
        // group can't be let go outside every group: that drop is refused and it springs back.
        // (Taking a video out of its group is what the row's menu is for.)
        GroupHeader? headerAbove = null;
        foreach (var row in _rows)
        {
            if (row is GroupHeader header)
            {
                headerAbove = header;
            }
            else if (row is DownloadedFile file && headerAbove is null && GroupOf(file) is not null)
            {
                MainThread.BeginInvokeOnMainThread(RebuildRows);
                return;
            }
        }

        var newMaster = new List<object>();
        var needsRebuild = false;
        for (var i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            newMaster.Add(row);
            if (row is GroupHeader { IsExpanded: false } collapsed)
            {
                // Its hidden videos stay with it. One dropped right under it joins the group, so
                // the group opens to show it.
                newMaster.AddRange(MembersOf(collapsed));
                if (i + 1 < _rows.Count && _rows[i + 1] is DownloadedFile)
                {
                    collapsed.IsExpanded = true;
                    needsRebuild = true;
                }
            }
        }

        _master = newMaster;
        RefreshStructure();
        if (needsRebuild)
        {
            MainThread.BeginInvokeOnMainThread(RebuildRows);
        }
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
                if (!_isSelectionMode && indexPath is not null && (int)indexPath.Item < _rows.Count)
                {
                    SetDraggingRow(_rows[(int)indexPath.Item] as DownloadedFile);
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
            // Already on its side when playback starts: go straight to full screen.
            UpdatePreviewForOrientation();
#if ANDROID
            _ = EnsureNotificationPermissionAsync();
            EnsureNativeCastButton();
#endif
        }
    }

#if ANDROID
    private bool _castButtonAttached;

    // Puts a "share to another screen" button into the player's own control bar (see
    // NativeCastButton). iOS needs nothing: AVPlayerViewController's native bar already has the
    // AirPlay button, which appears whenever an AirPlay screen is in range.
    private void EnsureNativeCastButton()
    {
        if (!_castButtonAttached)
        {
            _castButtonAttached = NativeCastButton.TryAttach(PreviewPlayer.Handler?.PlatformView);
        }
    }

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

    // Whether the preview is currently expanded to full screen. Tracked so a size change that
    // doesn't change the orientation (hiding the bars grows the page, for one) does nothing.
    private bool _isPreviewFullscreen;

    // Rotating the device changes the page's own size, so the orientation is read from that
    // (wider than tall = landscape) rather than from DeviceDisplay.MainDisplayInfoChanged: on
    // Android that event is driven by the physical orientation sensor, so it depends on sensor
    // behaviour (and doesn't fire for a rotation that is forced or locked), while SizeChanged
    // fires for every rotation on both platforms.
    private void OnPageSizeChanged(object? sender, EventArgs e) => UpdatePreviewForOrientation();

    // A rotation produces several size changes in quick succession, sometimes with an
    // intermediate size before the final one - reacting to every one as it arrives could flip
    // full screen on, then back off. Debouncing to the orientation that's still current a moment
    // after the changes stop avoids acting on those transient readings.
    private void UpdatePreviewForOrientation()
    {
        if (!PreviewContainer.IsVisible || Width <= 0 || Height <= 0)
        {
            return;
        }

        _rotationDebounceCts?.Cancel();

        var isLandscape = Width > Height;
        if (isLandscape == _isPreviewFullscreen)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _rotationDebounceCts = cts;
        _ = DebounceRotationAsync(isLandscape, cts.Token);
    }

    private async Task DebounceRotationAsync(bool isLandscape, CancellationToken token)
    {
        try
        {
            await Task.Delay(80, token);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        if (token.IsCancellationRequested)
        {
            return;
        }

        MainThread.BeginInvokeOnMainThread(() => SetPreviewFullscreen(isLandscape, animated: true));
    }

    // Expands the preview across every row of the page's own Grid (rather than just its normal
    // Row="0" slot) instead of navigating to a different page - the video, and the rest of the
    // page underneath it, never stop being exactly where they were.
    private int _coverVersion;

    private void SetPreviewFullscreen(bool fullscreen, bool animated = false)
    {
        var changed = fullscreen != _isPreviewFullscreen;
        _isPreviewFullscreen = fullscreen;

        // Everything below changes the layout in several steps (rows, padding, bars). Drawn one
        // after the other that reads as a jump, so while it happens a black cover hides the
        // in-between states, and it is faded away once they are all in place.
        var covered = animated && changed;
        if (covered)
        {
            _coverVersion++;
            TransitionCover.CancelAnimations();
            TransitionCover.Opacity = 1;
            TransitionCover.IsVisible = true;
        }
        Grid.SetRowSpan(PreviewContainer, fullscreen ? 3 : 1);
        PreviewContainer.HeightRequest = fullscreen ? -1 : 210;
        PreviewContainer.Margin = fullscreen ? new Thickness(0) : new Thickness(0, 0, 0, 12);
        Shell.SetNavBarIsVisible(this, !fullscreen);
        Shell.SetTabBarIsVisible(this, !fullscreen);

        if (!changed)
        {
            return;
        }

        // The real thing: no page padding, content allowed under the notch and the bars, and the
        // system bars (status bar, navigation buttons / home indicator) hidden.
        RootGrid.Padding = fullscreen ? new Thickness(0) : _normalRootPadding;
        RootGrid.SafeAreaEdges = fullscreen ? SafeAreaEdges.None : _normalSafeAreaEdges;
        FullScreenMode.SetSystemBarsHidden(fullscreen);
#if IOS
        Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific.Page.SetUseSafeArea(this, fullscreen ? false : _normalUsesSafeArea);
        Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific.Page.SetPrefersStatusBarHidden(
            this,
            fullscreen
                ? Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific.StatusBarHiddenMode.True
                : Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific.StatusBarHiddenMode.Default);
        Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific.Page.SetPrefersHomeIndicatorAutoHidden(this, fullscreen);
#endif

        if (covered)
        {
            _ = RevealAfterTransitionAsync();
        }
    }

    private async Task RevealAfterTransitionAsync()
    {
        var version = _coverVersion;

        // A moment for the bars to finish hiding/showing before the cover starts to go.
        await Task.Delay(120);
        if (version != _coverVersion)
        {
            return;
        }

        await TransitionCover.FadeToAsync(0, 180);
        if (version == _coverVersion)
        {
            TransitionCover.IsVisible = false;
        }
    }

    private void ClosePreview()
    {
        if (!PreviewContainer.IsVisible)
        {
            return;
        }

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
