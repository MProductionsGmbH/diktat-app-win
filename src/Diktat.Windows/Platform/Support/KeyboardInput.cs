using System.Runtime.InteropServices;
using Diktat.Windows.Platform.Native;

namespace Diktat.Windows.Platform;

/// Synthetic keyboard input through SendInput. Every event carries <see cref="ExtraInfoTag"/>
/// so Diktat's own keyboard hook can recognise it (it ignores injected events anyway).
internal static class KeyboardInput
{
    /// dwExtraInfo of every event Diktat injects ("DIKT").
    public static readonly IntPtr ExtraInfoTag = new(0x44494B54);

    private static readonly int InputSize = Marshal.SizeOf<Win32.INPUT>();

    public static readonly int[] ModifierKeys =
    [
        VK.LControl, VK.RControl, VK.LMenu, VK.RMenu, VK.LShift, VK.RShift, VK.LWin, VK.RWin,
    ];

    /// Keys whose events carry the extended flag (right Ctrl/Alt, Win, navigation keys ...).
    public static bool IsExtendedKey(int vk) => vk is VK.RControl or VK.RMenu or VK.LWin or VK.RWin or VK.Apps
        or 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 // PgUp, PgDn, End, Home, arrows
        or 0x2D or 0x2E or 0x6F or 0x90 or 0x2C; // Insert, Delete, Divide, NumLock, PrintScreen

    public static Win32.INPUT Key(int vk, bool up, bool? extended = null, ushort? scanCode = null)
    {
        uint flags = up ? Win32.KEYEVENTF_KEYUP : 0;
        if (extended ?? IsExtendedKey(vk)) flags |= Win32.KEYEVENTF_EXTENDEDKEY;
        ushort scan = scanCode ?? (ushort)Win32.MapVirtualKey((uint)vk, Win32.MAPVK_VK_TO_VSC);
        return new Win32.INPUT
        {
            type = Win32.INPUT_KEYBOARD,
            U = new Win32.InputUnion
            {
                ki = new Win32.KEYBDINPUT
                {
                    wVk = (ushort)vk,
                    wScan = scan,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = ExtraInfoTag,
                },
            },
        };
    }

    /// Down and up of the unassigned mask key: keeps the Start menu / menu bar from opening
    /// when Win or Alt is released next.
    public static IEnumerable<Win32.INPUT> MenuMask()
    {
        yield return Key(VK.MenuMask, up: false, extended: false, scanCode: 0);
        yield return Key(VK.MenuMask, up: true, extended: false, scanCode: 0);
    }

    /// Sends the events in one call, so no physical input lands between them.
    /// Returns false when Windows accepted fewer events than given (blocked by UIPI or the
    /// desktop is locked). Note: UIPI blocking is not always reported.
    public static bool Send(IEnumerable<Win32.INPUT> inputs)
    {
        var array = inputs as Win32.INPUT[] ?? inputs.ToArray();
        if (array.Length == 0) return true;
        uint sent = Win32.SendInput((uint)array.Length, array, InputSize);
        return sent == array.Length;
    }

    public static bool IsPhysicallyDown(int vk) => (Win32.GetAsyncKeyState(vk) & 0x8000) != 0;

    /// The modifier keys (left/right specific) that are down right now.
    public static List<int> HeldModifiers() => ModifierKeys.Where(IsPhysicallyDown).ToList();

    /// Ctrl+V. Modifiers still held (per GetAsyncKeyState) are released first, with a mask key
    /// in front so a released Alt or Win does not open the menu bar or the Start menu.
    public static bool SendPaste()
    {
        var inputs = new List<Win32.INPUT>();
        var held = HeldModifiers();
        if (held.Any(vk => vk is VK.LMenu or VK.RMenu or VK.LWin or VK.RWin)) inputs.AddRange(MenuMask());
        foreach (var vk in held) inputs.Add(Key(vk, up: true));
        inputs.Add(Key(VK.LControl, up: false));
        inputs.Add(Key(VK.V, up: false));
        inputs.Add(Key(VK.V, up: true));
        inputs.Add(Key(VK.LControl, up: true));
        return Send(inputs);
    }

    /// Waits until no modifier key is physically held (the user may still hold the shortcut),
    /// at most <paramref name="timeout"/>. True when all modifiers are up.
    public static async Task<bool> WaitForModifiersReleasedAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (true)
        {
            if (HeldModifiers().Count == 0) return true;
            if (Environment.TickCount64 >= deadline) return false;
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }
}
