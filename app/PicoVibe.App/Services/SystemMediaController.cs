using System.Runtime.InteropServices;

namespace PicoVibe.App.Services;

/// <summary>Sends Windows global media commands without targeting a specific player.</summary>
public sealed class WindowsSystemMediaController : ISystemMediaController
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventExtendedKey = 0x0001;
    private const uint KeyEventKeyUp = 0x0002;
    private const ushort VirtualKeyMediaNextTrack = 0xB0;

    public bool SendNextTrack()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var inputs = new[]
        {
            KeyboardInput(VirtualKeyMediaNextTrack, KeyEventExtendedKey),
            KeyboardInput(VirtualKeyMediaNextTrack, KeyEventExtendedKey | KeyEventKeyUp),
        };
        var inputSize = Marshal.SizeOf<NativeInput>();
        var inserted = SendInput((uint)inputs.Length, inputs, inputSize);
        if (inserted == inputs.Length)
        {
            return true;
        }

        var error = Marshal.GetLastWin32Error();
        if (inserted == 1)
        {
            var release = new[]
            {
                KeyboardInput(VirtualKeyMediaNextTrack, KeyEventExtendedKey | KeyEventKeyUp),
            };
            _ = SendInput(1, release, inputSize);
        }

        AppLog.Info($"SendInput accepted {inserted}/2 next-track events; Win32 error {error}.");
        return false;
    }

    private static NativeInput KeyboardInput(ushort virtualKey, uint flags) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion
        {
            Keyboard = new NativeKeyboardInput
            {
                VirtualKey = virtualKey,
                Flags = flags,
            },
        },
    };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(
        uint inputCount,
        [In] NativeInput[] inputs,
        int inputSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public NativeMouseInput Mouse;

        [FieldOffset(0)]
        public NativeKeyboardInput Keyboard;

        [FieldOffset(0)]
        public NativeHardwareInput Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeKeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeHardwareInput
    {
        public uint Message;
        public ushort ParameterLow;
        public ushort ParameterHigh;
    }
}
