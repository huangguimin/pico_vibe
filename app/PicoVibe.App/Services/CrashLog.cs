namespace PicoVibe.App.Services;

/// <summary>
/// Leaves evidence when the resident broker dies. Without this a crash looks
/// exactly like a wedged device: the strip holds its last frame (or drops to
/// the firmware profile when the VIBE lease expires) and the log ends
/// mid-sentence with nothing to explain it.
/// </summary>
public static class CrashLog
{
    private static int installed;

    /// <summary>
    /// Safe to call more than once; only the first call subscribes.
    /// </summary>
    public static void Install(string surface)
    {
        if (Interlocked.CompareExchange(ref installed, 1, 0) != 0)
        {
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLog.Fatal(
                $"{surface} is going down on an unhandled exception" +
                (args.IsTerminating ? "." : "; the runtime is not terminating."),
                args.ExceptionObject as Exception);

        // A clean exit and a process that simply vanished look identical in
        // the log otherwise, which is exactly the ambiguity that makes a dead
        // broker hard to tell apart from a wedged device.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            AppLog.InfoNow($"{surface} exited.");

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            // The process survives this one, so it is not fatal, but it still
            // has to reach the log: a background loop that died quietly is how
            // the lights stop with no visible cause.
            AppLog.Error($"{surface} left a background task exception unobserved.", args.Exception);
            args.SetObserved();
        };
    }
}
