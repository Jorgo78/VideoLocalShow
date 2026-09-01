namespace VideoLocalShow;

public partial class InfoPage : ContentPage
{
    public InfoPage()
    {
        InitializeComponent();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        AppNameLabel.Text = AppInfo.Current.Name;
        VersionLabel.Text = $"Versione: {AppInfo.Current.VersionString}";
        BuildLabel.Text = $"Build: {AppInfo.Current.BuildString}";
        PackageLabel.Text = $"Package: {AppInfo.Current.PackageName}";

        PlatformLabel.Text = $"{DeviceInfo.Current.Platform} ({DeviceInfo.Current.Idiom})";
        OsVersionLabel.Text = $"Versione OS: {DeviceInfo.Current.VersionString}";
        ModelLabel.Text = $"Modello: {DeviceInfo.Current.Model}";
        ManufacturerLabel.Text = $"Produttore: {DeviceInfo.Current.Manufacturer}";
        IdiomLabel.Text = $"Tipo: {DeviceInfo.Current.Idiom}";
        DeviceTypeLabel.Text = $"Ambiente: {DeviceInfo.Current.DeviceType}";

        var display = DeviceDisplay.Current.MainDisplayInfo;
        ScreenSizeLabel.Text = $"{display.Width:0}x{display.Height:0} px · densità {display.Density:0.0}";
        OrientationLabel.Text = $"Orientamento: {display.Orientation}";

        try
        {
            DownloadFolderLabel.Text = DownloadStorage.GetFolder();
        }
        catch (Exception ex)
        {
            DownloadFolderLabel.Text = $"Errore: {ex.Message}";
        }
    }
}
