using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using AspireWithMaui.MauiClient.Services;

namespace AspireWithMaui.MauiClient;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

		// Add service defaults & Aspire components.
		builder.AddServiceDefaults();

		// Register services
		builder.Services.AddSingleton<MainPage>();
		builder.Services.AddSingleton<IWeatherService, WeatherService>();

		// Configure HTTP client for weather API
		builder.Services.AddHttpClient<IWeatherService, WeatherService>(client =>
		{
			// This will be resolved via service discovery when running with Aspire
			// Use https+http:// scheme to allow fallback to HTTP when HTTPS is not available
			// (e.g., when running on Android emulator without dev tunnels)
			client.BaseAddress = new Uri("https+http://webapi");
		});

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
