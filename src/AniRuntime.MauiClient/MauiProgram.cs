using Microsoft.Extensions.Logging;

namespace AniRuntime.MauiClient;

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

        // Audio services — platform-specific implementations
#if ANDROID
        builder.Services.AddSingleton<IAudioCaptureService, AudioCaptureService>();
        builder.Services.AddSingleton<IAudioPlaybackService, AudioPlaybackService>();
#endif

        // Main page (constructor-injected with audio services)
        builder.Services.AddTransient<MainPage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
