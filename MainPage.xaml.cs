using System.Collections.ObjectModel;
using CommunityToolkit.Maui.Alerts;
using CommunityToolkit.Maui.Core;
using VideoLocalShow.Models;
using YoutubeExplode;
using YoutubeExplode.Common;
using YoutubeExplode.Search;
using YoutubeExplode.Videos;
using YoutubeExplode.Videos.Streams;

namespace VideoLocalShow;

public partial class MainPage : ContentPage
{
    private const int SearchPageSize = 20;

    private readonly YoutubeClient _youtube = new();
    private readonly ObservableCollection<SearchResultOption> _searchResults = [];
    private readonly ObservableCollection<ActiveDownload> _activeDownloads = [];

    private IAsyncEnumerator<VideoSearchResult>? _searchEnumerator;
    private bool _isLoadingMoreResults;

    public MainPage()
    {
        InitializeComponent();
        SearchResultsList.ItemsSource = _searchResults;
        ActiveDownloadsList.ItemsSource = _activeDownloads;

        // Kept subscribed for the page's whole lifetime, not just while it's the visible tab -
        // sharing a link in while the Downloads tab is open should still search/download right
        // away rather than waiting for the user to switch back to this tab first.
        DeepLinkService.LinkReceived += OnDeepLinkReceived;
    }

    // Sharing a link in cold-starts the app through a brand new Activity/Window/Shell instance,
    // and MainActivity hands the URL off (buffered in DeepLinkService, since nothing has
    // subscribed yet) before that Shell has finished attaching this page - acting on it directly
    // from the constructor risked running GoToAsync/SearchAsync against a page whose handlers
    // and Shell.Current aren't fully wired up yet. OnAppearing fires once this page is actually
    // part of a realized, current Shell, which is a safe point to pick up anything buffered.
    protected override void OnAppearing()
    {
        base.OnAppearing();

        if (DeepLinkService.ConsumePendingUrl() is { } pendingUrl)
        {
            ShareDebugLog.Append($"MainPage.OnAppearing found pending url: {pendingUrl}");
            _ = HandleIncomingLinkAsync(pendingUrl);
        }
    }

    private void OnDeepLinkReceived(string url)
    {
        ShareDebugLog.Append($"MainPage.OnDeepLinkReceived: {url}");
        MainThread.BeginInvokeOnMainThread(() => _ = HandleIncomingLinkAsync(url));
    }

    // A link shared in from another app - YouTube's own "Condividi" sheet, a browser, a chat -
    // skips the manual paste-and-tap step entirely: it runs straight through the same
    // resolve-and-download-immediately path a pasted link takes from the search bar. Wrapped in
    // its own try/catch since it always runs fire-and-forget (there is no caller left to await
    // it, or show an error for it) - without this, any exception here (a bad Shell navigation, a
    // parsing surprise) would vanish as an unobserved task exception instead of being reported.
    private async Task HandleIncomingLinkAsync(string url)
    {
        ShareDebugLog.Append($"HandleIncomingLinkAsync entered with: {url}");
        try
        {
            // Some apps' share sheets tack on surrounding text, e.g. "Guarda questo video:
            // https://youtu.be/XXXX tramite YouTube" - pull out just the URL so parsing below
            // doesn't choke on the rest of the sentence.
            var match = System.Text.RegularExpressions.Regex.Match(url, @"https?://\S+");
            var link = match.Success ? match.Value : url.Trim();
            ShareDebugLog.Append($"Extracted link: {link}");

            if (string.IsNullOrWhiteSpace(link))
            {
                ShareDebugLog.Append("Link is blank, returning.");
                return;
            }

            // Best-effort only: a share intent opens this page in a brand new window rather than
            // reusing one that might already be open on the Downloads tab, and Shell.Current
            // itself throws ("unable to determine the current Shell instance") the moment more
            // than one window exists - the property access is ambiguous across windows, not just
            // the navigation. That's just a missed tab-switch, not a reason to also give up on
            // the actual search/download below, so it's isolated in its own try.
            try
            {
                if (Shell.Current is not null)
                {
                    await Shell.Current.GoToAsync("//MainPage");
                }
            }
            catch
            {
                // Ignored - this page is still perfectly usable without switching tabs to it.
            }

            await HandleSharedLinkAsync(link);
        }
        catch (Exception ex)
        {
            ShareDebugLog.Append($"HandleIncomingLinkAsync EXCEPTION: {ex}");
            ShowStatus($"Impossibile aprire il link condiviso: {ex.Message}");
        }
    }

    private async Task HandleSharedLinkAsync(string link)
    {
        if (SearchResultsSection.IsVisible)
        {
            ClearSearchResults();
        }

        var videoId = VideoId.TryParse(link);
        ShareDebugLog.Append($"VideoId.TryParse result: {videoId?.ToString() ?? "(null - will search instead)"}");

        if (videoId is not null)
        {
            UrlEntry.Text = string.Empty;
            ShareDebugLog.Append("Calling ResolveAndDownloadAsync...");
            await ResolveAndDownloadAsync(link, titleHint: null);
            ShareDebugLog.Append("ResolveAndDownloadAsync returned.");
        }
        else
        {
            UrlEntry.Text = link;
            await SearchAsync(link);
        }
    }

    // This one button doubles as both ends of a search: a magnifying glass before results are
    // showing, and an "X" that clears them back to the empty state once they are - there is
    // never a moment where both actions would make sense at once, so one slot serves both.
    private async void OnSearchClicked(object? sender, EventArgs e)
    {
        if (SearchResultsSection.IsVisible)
        {
            ClearSearchResults();
            return;
        }

        KeyboardService.Hide(UrlEntry);

        var input = UrlEntry.Text?.Trim();
        if (string.IsNullOrWhiteSpace(input))
        {
            ShowStatus("Incolla un link o scrivi cosa cercare.");
            return;
        }

        // A direct video link/id has nothing to pick between - resolve it and download the
        // best quality straight away, same as a checked search result.
        if (VideoId.TryParse(input) is not null)
        {
            UrlEntry.Text = string.Empty;
            await ResolveAndDownloadAsync(input, titleHint: null);
            return;
        }

        await SearchAsync(input);
    }

    private void ClearSearchResults()
    {
        _searchEnumerator = null;
        _searchResults.Clear();
        SearchResultsSection.IsVisible = false;
        UrlEntry.Text = string.Empty;
        UpdateSearchActionIcon();
    }

    private void UpdateSearchActionIcon() =>
        SearchButton.Source = SearchResultsSection.IsVisible ? "icon_close.png" : "icon_search.png";

    private async Task SearchAsync(string query)
    {
        _searchResults.Clear();
        SearchResultsSection.IsVisible = true;
        UpdateSearchActionIcon();
        SetBusy(true);

        try
        {
            // Force the network calls onto a background thread: on Android, HttpClient's
            // DNS/connection setup can occasionally complete synchronously and run inline
            // on whichever thread called it, which throws NetworkOnMainThreadException if
            // that thread is the UI thread (e.g. when invoked directly from a Click handler).
            _searchEnumerator = await Task.Run(() => _youtube.Search.GetVideosAsync(query).GetAsyncEnumerator());
            await LoadMoreSearchResultsAsync();

            if (_searchResults.Count == 0)
            {
                ShowStatus("Nessun video trovato per questa ricerca.");
            }
            else
            {
                // Reusing the same ObservableCollection instance (cleared and refilled, rather
                // than assigned as a new ItemsSource) can leave the CollectionView's scroll
                // position wherever a previous search left it - jump back to the top explicitly.
                SearchResultsList.ScrollTo(0, position: ScrollToPosition.Start, animate: false);
            }
        }
        catch (Exception ex)
        {
            ShowStatus($"Ricerca non riuscita: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnSearchResultsThresholdReached(object? sender, EventArgs e) => await LoadMoreSearchResultsAsync();

    private async Task LoadMoreSearchResultsAsync()
    {
        if (_isLoadingMoreResults || _searchEnumerator is null)
        {
            return;
        }

        _isLoadingMoreResults = true;
        LoadingMoreResultsIndicator.IsRunning = true;
        LoadingMoreResultsIndicator.IsVisible = true;

        try
        {
            var batch = await Task.Run(async () =>
            {
                var list = new List<VideoSearchResult>();
                while (list.Count < SearchPageSize && await _searchEnumerator.MoveNextAsync())
                {
                    list.Add(_searchEnumerator.Current);
                }

                return list;
            });

            foreach (var video in batch)
            {
                _searchResults.Add(new SearchResultOption
                {
                    VideoId = video.Id,
                    Title = video.Title,
                    ChannelTitle = video.Author.ChannelTitle,
                    DurationText = video.Duration is { } duration ? duration.ToString(@"hh\:mm\:ss").TrimStart('0', ':') : "Diretta",
                    ThumbnailUrl = video.Thumbnails.GetWithHighestResolution()?.Url,
                    IsAlreadyDownloaded = IsAlreadyDownloaded(video.Title),
                    // Tapping the row's download icon resolves and downloads that single video
                    // immediately - there is no selection step, one tap is the whole action.
                    // IsResolving is set synchronously, before the (awaited) resolve even starts,
                    // so the icon is already hidden by the time this handler returns - a second,
                    // impatient tap has nothing left to hit.
                    DownloadCommand = new Command<SearchResultOption>(o =>
                    {
                        o!.IsResolving = true;
                        _ = ResolveAndDownloadAsync(o.VideoId, o.Title, o);
                    })
                });
            }
        }
        catch (Exception ex)
        {
            ShowStatus($"Impossibile caricare altri risultati: {ex.Message}");
        }
        finally
        {
            _isLoadingMoreResults = false;
            LoadingMoreResultsIndicator.IsRunning = false;
            LoadingMoreResultsIndicator.IsVisible = false;
        }
    }

    // Only the video+audio track is ever produced - no format list is shown to the user.
    // Resolves the video and picks the best quality this platform can play, then starts the
    // download. A video downloaded from its search-result row shows its progress and stop
    // button right in that row; one entered as a direct link has no row to show it in, so it
    // gets one in the "download in corso" list instead.
    private async Task ResolveAndDownloadAsync(string videoIdOrUrl, string? titleHint, SearchResultOption? searchItem = null)
    {
        // A search-result row shows its own IsResolving spinner in place of its download icon
        // during this same wait, so it needs nothing more here - but a direct link (pasted, or
        // handed in by a shared link) has no row of its own, and resolving a video (fetching its
        // info and stream manifest from YouTube) can take a couple of seconds. Without this, that
        // wait looks identical to nothing having happened at all, which reads as the download
        // having silently failed to start rather than just being on its way.
        if (searchItem is null)
        {
            SetBusy(true);
        }

        try
        {
            var (video, manifest) = await Task.Run(async () =>
            {
                var v = await _youtube.Videos.GetAsync(videoIdOrUrl);
                var m = await _youtube.Videos.Streams.GetManifestAsync(v.Id);
                return (v, m);
            });

            var option = BuildBestVideoAudioOption(manifest);
            if (option is null)
            {
                if (searchItem is not null)
                {
                    searchItem.IsResolving = false;
                }

                ShowStatus($"Nessun formato scaricabile trovato per \"{titleHint ?? video.Title}\".");
                return;
            }

            if (searchItem is not null)
            {
                StartDownloadForSearchResult(searchItem, option);
            }
            else
            {
                StartDownload(video.Title, option);
            }
        }
        catch (Exception ex)
        {
            if (searchItem is not null)
            {
                searchItem.IsResolving = false;
            }

            ShowStatus($"Impossibile analizzare \"{titleHint ?? videoIdOrUrl}\": {ex.Message}");
        }
        finally
        {
            if (searchItem is null)
            {
                SetBusy(false);
            }
        }
    }

    private StreamOption? BuildBestVideoAudioOption(StreamManifest manifest)
    {
        var audioOnlyStreams = manifest.GetAudioOnlyStreams().ToList();
        var bestAudio = audioOnlyStreams
            .OrderByDescending(s => s.Bitrate.BitsPerSecond)
            .FirstOrDefault();

        // YouTube usually only offers muxed (video+audio in one stream) progressive files up
        // to 720p; higher resolutions are only available as separate video-only and audio-only
        // streams. When we can mux those two back together on this platform, that combination
        // beats any native muxed stream, so it's tried first.
        if (VideoMuxer.IsSupported && bestAudio is not null)
        {
            // Only consider containers this platform can actually read: iOS has no WebM
            // support, so offering a WebM stream there would produce a download that fails
            // at the merging step.
            var bestVideoOnly = manifest.GetVideoOnlyStreams()
                .Where(s => VideoMuxer.SupportedContainers.Contains(s.Container.Name))
                .OrderByDescending(s => s.VideoResolution.Area)
                .ThenByDescending(s => s.VideoQuality.Framerate)
                .FirstOrDefault();

            var matchingAudio = bestVideoOnly is null
                ? null
                : audioOnlyStreams
                    .Where(a => a.Container.Name == bestVideoOnly.Container.Name)
                    .OrderByDescending(a => a.Bitrate.BitsPerSecond)
                    .FirstOrDefault()
                  ?? audioOnlyStreams
                    .Where(a => VideoMuxer.SupportedContainers.Contains(a.Container.Name))
                    .OrderByDescending(a => a.Bitrate.BitsPerSecond)
                    .FirstOrDefault();

            if (bestVideoOnly is not null && matchingAudio is not null)
            {
                var useWebm = bestVideoOnly.Container.Name == "webm" && matchingAudio.Container.Name == "webm";
                var extension = useWebm ? "webm" : "mp4";

                return new StreamOption
                {
                    Stream = bestVideoOnly,
                    AudioStreamForMuxing = matchingAudio,
                    FileExtension = extension
                };
            }
        }

        var bestMuxed = manifest.GetMuxedStreams()
            .OrderByDescending(s => s.VideoResolution.Area)
            .FirstOrDefault();

        if (bestMuxed is null)
        {
            return null;
        }

        return new StreamOption
        {
            Stream = bestMuxed,
            FileExtension = bestMuxed.Container.Name
        };
    }

    // Used for a direct-link download: there is no search-result row to show progress in, so
    // it gets one of its own in the "download in corso" list instead.
    private void StartDownload(string title, StreamOption option)
    {
        var cts = new CancellationTokenSource();
        var entry = new ActiveDownload
        {
            Label = title,
            StatusText = $"\"{title}\" · in coda...",
            CancelCommand = new Command(() => cts.Cancel())
        };

        _activeDownloads.Add(entry);
        ActiveDownloadsHeader.IsVisible = true;
        ActiveDownloadsList.IsVisible = true;

        _ = RunDownloadCoreAsync(
            title,
            option,
            cts,
            setProgress: p => entry.Progress = p,
            setStatus: s => entry.StatusText = s,
            onFinished: _ =>
            {
                _activeDownloads.Remove(entry);
                if (_activeDownloads.Count == 0)
                {
                    ActiveDownloadsHeader.IsVisible = false;
                    ActiveDownloadsList.IsVisible = false;
                }
            });
    }

    // Used for a video downloaded straight from its search-result row: its own row shows
    // the progress bar and stop button, so there is no separate list entry for it.
    private void StartDownloadForSearchResult(SearchResultOption item, StreamOption option)
    {
        var cts = new CancellationTokenSource();
        item.StopDownloadCommand = new Command(() => cts.Cancel());
        item.DownloadProgress = 0;
        item.IsResolving = false;
        item.IsDownloading = true;

        _ = RunDownloadCoreAsync(
            item.Title,
            option,
            cts,
            setProgress: p => item.DownloadProgress = p,
            setStatus: null,
            onFinished: success =>
            {
                item.IsDownloading = false;
                if (success)
                {
                    item.IsDownloadComplete = true;
                }
            });
    }

    private async Task RunDownloadCoreAsync(
        string title,
        StreamOption option,
        CancellationTokenSource cts,
        Action<double> setProgress,
        Action<string>? setStatus,
        Action<bool> onFinished)
    {
        var fileName = $"{SanitizeFileName(title)}.{option.FileExtension}";
        var destinationPath = DownloadStorage.GetUniqueFilePath(fileName);

        setStatus?.Invoke($"\"{title}\" · download in corso...");

        string? tempVideoPath = null;
        string? tempAudioPath = null;
        var success = false;

        try
        {
            if (option.AudioStreamForMuxing is { } audioStream)
            {
                // Name the temporary files after their real container: AVFoundation on iOS
                // identifies a file's format from its extension, so an invented one like
                // ".video" would leave the stream unreadable at the merging step.
                tempVideoPath = Path.Combine(FileSystem.CacheDirectory, $"{Guid.NewGuid()}.{option.Stream.Container.Name}");
                tempAudioPath = Path.Combine(FileSystem.CacheDirectory, $"{Guid.NewGuid()}.{audioStream.Container.Name}");

                // Video and audio are downloaded separately, then muxed together locally - YouTube
                // doesn't offer a single combined stream at this quality. Weight each stream's
                // share of the progress bar by its size, and reserve the last bit for muxing.
                var videoShare = (double)option.Stream.Size.Bytes / (option.Stream.Size.Bytes + audioStream.Size.Bytes);
                var videoProgress = new Progress<double>(p =>
                    MainThread.BeginInvokeOnMainThread(() => setProgress(p * videoShare * 0.9)));
                var audioProgress = new Progress<double>(p =>
                    MainThread.BeginInvokeOnMainThread(() => setProgress((videoShare + p * (1 - videoShare)) * 0.9)));

                await Task.Run(async () =>
                {
                    await using (var videoFile = File.Create(tempVideoPath))
                    {
                        await _youtube.Videos.Streams.CopyToAsync(option.Stream, videoFile, videoProgress, cts.Token);
                    }

                    await using (var audioFile = File.Create(tempAudioPath))
                    {
                        await _youtube.Videos.Streams.CopyToAsync(audioStream, audioFile, audioProgress, cts.Token);
                    }
                });

                setStatus?.Invoke($"\"{title}\" · unione di video e audio...");
                await VideoMuxer.MuxAsync(tempVideoPath, tempAudioPath, destinationPath, option.FileExtension == "webm", cts.Token);
                setProgress(1);
            }
            else
            {
                var progress = new Progress<double>(p =>
                    MainThread.BeginInvokeOnMainThread(() => setProgress(p)));

                // Save straight into the app's own Downloads folder - no picker, no confirmation.
                // We own the destination FileStream ourselves (CopyToAsync instead of the file-path
                // DownloadAsync overload) so it is guaranteed closed - via "await using" - as soon as
                // the copy finishes, rather than relying on the library's own internal timing.
                await Task.Run(async () =>
                {
                    await using var destination = File.Create(destinationPath);
                    await _youtube.Videos.Streams.CopyToAsync(option.Stream, destination, progress, cts.Token);
                });
            }

            success = true;
        }
        catch (OperationCanceledException)
        {
            ShowStatus($"Download di \"{title}\" annullato.");
            await TryDeletePartialFileAsync(destinationPath);
        }
        catch (Exception ex)
        {
            ShowStatus($"Download di \"{title}\" non riuscito: {ex.Message}");
            await TryDeletePartialFileAsync(destinationPath);
        }
        finally
        {
            if (tempVideoPath is not null)
            {
                await TryDeletePartialFileAsync(tempVideoPath);
            }

            if (tempAudioPath is not null)
            {
                await TryDeletePartialFileAsync(tempAudioPath);
            }

            onFinished(success);
            cts.Dispose();
        }
    }

    private static async Task TryDeletePartialFileAsync(string path)
    {
        // On a cancelled write, the destination FileStream's handle can take a moment
        // to be released after "await using" disposes it - retry briefly instead of
        // silently leaving a (near-)complete partial file behind.
        const int maxAttempts = 5;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                return;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                await Task.Delay(200);
            }
            catch (IOException)
            {
                // Best-effort cleanup only.
            }
        }
    }

    // Path.GetInvalidFileNameChars() is platform-dependent and on Android returns almost
    // nothing (just '/' and NUL), so characters like '?' and '#' survive into the file name.
    // The player then parses the path as a URI and treats everything after a '?' as a query
    // string, truncating the path and failing to open a perfectly good file. Strip a fixed
    // set of characters that are unsafe either as a file name or inside a URI.
    private static readonly char[] UnsafeFileNameChars =
        Path.GetInvalidFileNameChars()
            .Concat(['?', '#', '|', '*', '<', '>', ':', '"', '\\', '/', '%'])
            .Distinct()
            .ToArray();

    private static string SanitizeFileName(string name)
    {
        var sanitized = new string(name.Where(c => !UnsafeFileNameChars.Contains(c) && !char.IsControl(c)).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "video" : sanitized;
    }

    // A previous download of the same title got a "(2)", "(3)", ... suffix from
    // DownloadStorage.GetUniqueFilePath to avoid overwriting it, so matching has to compare
    // against the base name rather than the exact file name.
    private static bool IsAlreadyDownloaded(string title)
    {
        var baseName = SanitizeFileName(title);

        return Directory.EnumerateFiles(DownloadStorage.GetFolder())
            .Select(Path.GetFileNameWithoutExtension)
            .Any(name => name == baseName || (name?.StartsWith($"{baseName} (", StringComparison.Ordinal) ?? false));
    }

    private void SetBusy(bool isBusy)
    {
        BusyIndicator.IsRunning = isBusy;
        BusyIndicator.IsVisible = isBusy;
        SearchButton.IsEnabled = !isBusy;
    }

    // Fire-and-forget: none of the call sites need to wait on a toast, and making every one of
    // them async just to await this would ripple outward for no benefit.
    private static void ShowStatus(string message, bool isError = true) =>
        _ = Toast.Make(message, isError ? ToastDuration.Long : ToastDuration.Short).Show();
}
