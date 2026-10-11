using Avalonia;
using PicoVibe.App.Services;

namespace PicoVibe.Mac;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        CrashLog.Install("Pico Vibe");
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
