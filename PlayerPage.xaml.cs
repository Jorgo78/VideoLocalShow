using CommunityToolkit.Maui.Views;

namespace VideoLocalShow;

public partial class PlayerPage : ContentPage
{
    private readonly string _filePath;
    private bool _hasVideo = true;

    public PlayerPage(string filePath, string title)
    {
        InitializeComponent();
        _filePath = filePath;
        TitleLabel.Text = title;
        Player.Source = MediaSource.FromFile(filePath);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        FullScreenMode.Enable();

        // Rotating into landscape re-lays out the window, which brings the system bars back;
        // re-hiding them on every layout change keeps the video truly full screen.
        SizeChanged += OnPageSizeChanged;

        _hasVideo = await MediaInspector.HasVideoTrackAsync(_filePath);
        AudioOnlyOverlay.IsVisible = !_hasVideo;

        // An audio-only file has nothing to show, so leave the screen as it was for it.
        if (!_hasVideo)
        {
            SizeChanged -= OnPageSizeChanged;
            FullScreenMode.Disable();
            return;
        }

        await Task.Delay(400);
        FullScreenMode.Reapply();
    }

    private void OnPageSizeChanged(object? sender, EventArgs e)
    {
        if (_hasVideo)
        {
            FullScreenMode.Reapply();
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        SizeChanged -= OnPageSizeChanged;
        FullScreenMode.Disable();
    }

    private async void OnCloseClicked(object? sender, EventArgs e)
    {
        Player.Stop();
        await Navigation.PopAsync();
    }

    protected override bool OnBackButtonPressed()
    {
        Player.Stop();
        Navigation.PopAsync();
        return true;
    }
}
