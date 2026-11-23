using Microsoft.Extensions.DependencyInjection;

namespace VideoLocalShow;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		var window = new Window(new AppShell());

		// Each time the app comes to the front (including the first launch), check whether the
		// iOS Share Extension has left a link behind. No-op on other platforms.
		window.Activated += (_, _) => DeepLinkService.PollAppGroup();

		return window;
	}
}