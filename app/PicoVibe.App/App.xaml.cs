using System.Threading;
using PicoVibe.Core.Ipc;

namespace PicoVibe.App;

public partial class App : System.Windows.Application
{
    private Mutex? instanceMutex;

    protected override async void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        const string mutexName = "Local\\PicoVibe.App.v1";
        instanceMutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
        if (!createdNew)
        {
            var client = new NamedPipeHookClient();
            await client.SendAsync(
                new HookRequest
                {
                    Platform = HookPlatform.Codex,
                    Event = HookEvent.SessionStarted,
                    Metadata = new Dictionary<string, string>
                    {
                        ["control"] = "show",
                    },
                },
                TimeSpan.FromSeconds(1));
            Shutdown();
            return;
        }

        var window = new MainWindow(e.Args.Contains("--autostart", StringComparer.OrdinalIgnoreCase));
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        try
        {
            instanceMutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
        }
        instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
