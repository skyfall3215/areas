using System.Runtime.InteropServices;
using SecondScreen.Core.Protocol;

namespace SecondScreen.Input;

public interface IKeyboardInjector
{
    bool InjectKey(KeyboardEventMessage keyEvent);
}

public sealed class Win32KeyboardInjector : IKeyboardInjector
{
    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT
    {
        [FieldOffset(0)]
        public uint type;

        [FieldOffset(8)]
        public KEYBDINPUT ki;
    }

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

    public bool InjectKey(KeyboardEventMessage keyEvent)
    {
        var input = new INPUT
        {
            type = INPUT_KEYBOARD
        };

        uint flags = 0;
        if (!keyEvent.IsKeyDown)
        {
            flags |= KEYEVENTF_KEYUP;
        }

        if (keyEvent.IsUnicode)
        {
            flags |= KEYEVENTF_UNICODE;
            input.ki = new KEYBDINPUT
            {
                wVk = 0,
                wScan = keyEvent.KeyCode,
                dwFlags = flags,
                time = 0,
                dwExtraInfo = 0
            };
        }
        else
        {
            input.ki = new KEYBDINPUT
            {
                wVk = keyEvent.KeyCode,
                wScan = 0,
                dwFlags = flags,
                time = 0,
                dwExtraInfo = 0
            };
        }

        uint sent = SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        return sent > 0;
    }
}

