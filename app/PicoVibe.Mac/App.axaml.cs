using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using PicoVibe.Core.Ipc;

namespace PicoVibe.Mac;

public partial class App : Application
{
    private Mutex? instanceMutex;
    private bool ownsInstanceMutex;
    private TrayIcon? trayIcon;
    private MainWindow? window;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            string mutexName = OperatingSystem.IsWindows()
                ? "Local\\PicoVibe.App.v1"
                : "PicoVibe.App.v1";
            instanceMutex = new Mutex(true, mutexName, out bool createdNew);
            ownsInstanceMutex = TryAcquireInstanceMutex(instanceMutex, createdNew);

            if (!ownsInstanceMutex)
            {
                if (TryShowExistingAsync(10).GetAwaiter().GetResult())
                {
                    DisposeInstanceMutex();
                    desktop.Shutdown();
                    base.OnFrameworkInitializationCompleted();
                    return;
                }

                // The old instance may have exited while the pipe retries were
                // in progress. Re-acquire the same mutex; never bypass it with
                // a second lock because that could create two device writers.
                ownsInstanceMutex = TryAcquireInstanceMutex(instanceMutex, createdNew: false);
                if (!ownsInstanceMutex)
                {
                    DisposeInstanceMutex();
                    desktop.Shutdown();
                    base.OnFrameworkInitializationCompleted();
                    return;
                }
            }

            try
            {
                bool autostart = desktop.Args?.Contains("--autostart", StringComparer.OrdinalIgnoreCase) == true;
                window = new MainWindow(autostart);
                window.ExitRequested += (_, _) => desktop.Shutdown();
                desktop.MainWindow = window;
                desktop.Exit += (_, _) => Cleanup();
                SetupTray();
            }
            catch
            {
                DisposeInstanceMutex();
                throw;
            }
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void SetupTray()
    {
        using var iconStream = AssetLoader.Open(new Uri("avares://PicoVibe/Assets/PicoVibe.png"));
        var icon = new WindowIcon(new Bitmap(iconStream));
        var menu = new NativeMenu();
        menu.Add(new NativeMenuItem("打开 Pico Vibe")
        {
            Command = new ActionCommand(() => window?.ShowFromTray()),
        });
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(new NativeMenuItem("退出")
        {
            Command = new ActionCommand(() => window?.RequestExit()),
        });
        trayIcon = new TrayIcon
        {
            Icon = icon,
            ToolTipText = "Pico Vibe",
            Menu = menu,
            IsVisible = true,
        };
        TrayIcon.SetIcons(this, new TrayIcons { trayIcon });
    }

    private void Cleanup()
    {
        trayIcon?.Dispose();
        DisposeInstanceMutex();
    }

    private static bool TryAcquireInstanceMutex(Mutex mutex, bool createdNew)
    {
        if (createdNew) return true;
        try
        {
            // A named mutex can outlive a crashed process briefly on macOS. Its
            // existence alone does not prove that another instance is healthy.
            return mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            // WaitOne grants ownership before reporting abandonment.
            return true;
        }
    }

    private static async Task<bool> TryShowExistingAsync(int attempts)
    {
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                var response = await new NamedPipeHookClient().SendAsync(
                    new HookRequest
                    {
                        Platform = HookPlatform.Codex,
                        Event = HookEvent.SessionStarted,
                        Metadata = new Dictionary<string, string> { ["control"] = "show" },
                    },
                    TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
                if (response is not null) return true;
            }
            catch
            {
            }

            if (attempt + 1 < attempts) await Task.Delay(100).ConfigureAwait(false);
        }
        return false;
    }

    private void DisposeInstanceMutex()
    {
        if (ownsInstanceMutex)
        {
            try { instanceMutex?.ReleaseMutex(); }
            catch (ApplicationException) { }
        }
        ownsInstanceMutex = false;
        instanceMutex?.Dispose();
        instanceMutex = null;
    }

    private sealed class ActionCommand(Action action) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }
}
