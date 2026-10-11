using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PicoVibe.App.Services;

/// <summary>
/// Holds an operating-system power assertion while the lights are running.
/// The board draws its power from the card slot, and once the machine sleeps
/// neither macOS nor Windows can bring the volume back without a physical
/// re-plug, so a user who leaves the lights on can ask for idle sleep to be
/// held off. Display sleep is deliberately still allowed.
/// </summary>
public sealed class SleepGuard : IDisposable
{
    private const uint AssertionLevelOn = 255;
    private const uint Utf8Encoding = 0x08000100;
    private const uint ContinuousExecution = 0x80000000;
    private const uint SystemRequiredExecution = 0x00000001;
    // macOS drops a non-ASCII assertion name: pmset then lists the assertion
    // with an empty name and nobody can tell who is holding the machine awake.
    private const string AssertionReason = "Pico Vibe is driving the LED strip";

    private readonly object gate = new();
    private uint macAssertion;
    private ManualResetEventSlim? windowsRelease;
    private bool held;

    /// <summary>True while an assertion is actually held.</summary>
    public bool Held
    {
        get
        {
            lock (gate)
            {
                return held;
            }
        }
    }

    public void Apply(bool enabled)
    {
        lock (gate)
        {
            if (enabled == held)
            {
                return;
            }

            held = enabled ? Acquire() : !Release();
        }
    }

    public void Dispose() => Apply(false);

    private bool Acquire()
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                return AcquireMac();
            }
            if (OperatingSystem.IsWindows())
            {
                AcquireWindows();
                return true;
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            AppLog.Error("This system exposes no power assertion API.", exception);
        }

        return false;
    }

    private bool Release()
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                if (macAssertion != 0)
                {
                    IOPMAssertionRelease(macAssertion);
                    macAssertion = 0;
                }
                return true;
            }
            if (OperatingSystem.IsWindows())
            {
                windowsRelease?.Set();
                windowsRelease = null;
                return true;
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            AppLog.Error("Releasing the power assertion failed.", exception);
        }

        return true;
    }

    private bool AcquireMac()
    {
        var type = CFStringCreateWithCString(IntPtr.Zero, "PreventUserIdleSystemSleep", Utf8Encoding);
        var reason = CFStringCreateWithCString(IntPtr.Zero, AssertionReason, Utf8Encoding);
        try
        {
            if (type == IntPtr.Zero || reason == IntPtr.Zero)
            {
                return false;
            }

            // kIOReturnSuccess is 0.
            return IOPMAssertionCreateWithName(type, AssertionLevelOn, reason, out macAssertion) == 0;
        }
        finally
        {
            if (type != IntPtr.Zero) CFRelease(type);
            if (reason != IntPtr.Zero) CFRelease(reason);
        }
    }

    /// <remarks>
    /// The Windows execution state is per thread, so the assertion has to live
    /// on a thread of its own that parks until the guard is released.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    private void AcquireWindows()
    {
        var release = new ManualResetEventSlim(false);
        var ready = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            SetThreadExecutionState(ContinuousExecution | SystemRequiredExecution);
            ready.Set();
            release.Wait();
            SetThreadExecutionState(ContinuousExecution);
            release.Dispose();
        })
        {
            IsBackground = true,
            Name = "PicoVibe sleep guard",
        };
        thread.Start();
        ready.Wait();
        ready.Dispose();
        windowsRelease = release;
    }

    [DllImport("/System/Library/Frameworks/IOKit.framework/IOKit")]
    private static extern int IOPMAssertionCreateWithName(
        IntPtr assertionType,
        uint assertionLevel,
        IntPtr assertionName,
        out uint assertionId);

    [DllImport("/System/Library/Frameworks/IOKit.framework/IOKit")]
    private static extern int IOPMAssertionRelease(uint assertionId);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFStringCreateWithCString(
        IntPtr allocator,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value,
        uint encoding);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(IntPtr reference);

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);
}
