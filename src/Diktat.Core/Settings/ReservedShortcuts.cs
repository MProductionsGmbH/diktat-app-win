namespace Diktat.Core.Settings;

/// Shortcuts that Windows and nearly every program use themselves, e.g. Strg + V. As a global
/// shortcut of Diktat (the main key is taken from every program) it would stop working
/// everywhere else, so the settings do not accept them (KeyCombo.isReserved of the Mac app).
public static class ReservedShortcuts
{
    // Virtual-key codes; letters are their capital ASCII code.
    private const int Tab = 0x09, Enter = 0x0D, Space = 0x20, Left = 0x25, Right = 0x27, Delete = 0x2E, F4 = 0x73;

    /// Strg (with or without Umschalt) plus: select all, copy, find, new, open, print, save,
    /// new tab, paste, close, cut, redo, undo, the next tab and closing a tab.
    private static readonly HashSet<int> CtrlKeys = ['A', 'C', 'F', 'N', 'O', 'P', 'S', 'T', 'V', 'W', 'X', 'Y', 'Z', Tab, F4];

    /// Alt (with or without Umschalt) plus: switching windows, closing a window, the window menu.
    private static readonly HashSet<int> AltKeys = [Tab, F4, Space];

    /// Strg + Win plus: virtual desktops, Narrator, on-screen keyboard.
    private static readonly HashSet<int> CtrlWinKeys = ['D', 'O', F4, Left, Right, Enter];

    /// True for the shortcuts Windows and nearly every program use. Win (with or without
    /// Umschalt) plus any key belongs to Windows (Win + E, Win + V, Win + Umschalt + S).
    /// Shortcuts of modifier keys alone are free.
    public static bool IsReserved(KeyCombo combo)
    {
        if (combo.Key is not int key) return false;
        if (combo.Ctrl && combo.Alt && key == Delete) return true;
        if (combo.Win && !combo.Ctrl && !combo.Alt) return true;
        if (combo.Ctrl && combo.Win && !combo.Alt && !combo.Shift) return CtrlWinKeys.Contains(key);
        if (combo.Win) return false;
        if (combo.Ctrl && !combo.Alt) return CtrlKeys.Contains(key);
        if (combo.Alt && !combo.Ctrl) return AltKeys.Contains(key);
        return false;
    }
}
