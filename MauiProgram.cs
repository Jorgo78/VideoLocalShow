using CommunityToolkit.Maui;
using Microsoft.Extensions.Logging;

namespace VideoLocalShow;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.UseMauiCommunityToolkit()
			// The foreground service is what keeps Android playing (and shows the playback
			// notification with its controls) once the app is in the background or the screen locks;
			// without it Android stops the player shortly after the app leaves the screen.
			.UseMauiCommunityToolkitMediaElement(isAndroidForegroundServiceEnabled: true)
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
