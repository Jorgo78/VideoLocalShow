namespace VideoLocalShow;

// TEMP DIAGNOSTIC - remove once iOS sharing is confirmed reliably working end to end.
public partial class LogPage : ContentPage
{
    public LogPage()
    {
        InitializeComponent();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        RefreshLog();
    }

    private void OnRefreshClicked(object? sender, EventArgs e) => RefreshLog();

    private void OnClearClicked(object? sender, EventArgs e)
    {
        ShareDebugLog.Clear();
        RefreshLog();
    }

    private void RefreshLog() => LogLabel.Text = ShareDebugLog.Read();
}
