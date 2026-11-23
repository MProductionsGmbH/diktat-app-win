using Diktat.Core.Settings;

namespace Diktat.Core.Tests;

// KeyComboTests of the macOS app, with the shortcuts of Windows.
public class ReservedShortcutsTests
{
    private const int Tab = 0x09, Space = 0x20, Left = 0x25, F4 = 0x73, F9 = 0x78;

    private static KeyCombo Combo(int key, bool ctrl = false, bool alt = false, bool shift = false, bool win = false) =>
        new() { Key = key, Ctrl = ctrl, Alt = alt, Shift = shift, Win = win };

    /// Strg + V as Diktat's global shortcut would stop pasting everywhere.
    [Fact]
    public void ShortcutsEveryProgramUsesAreReserved()
    {
        Assert.True(ReservedShortcuts.IsReserved(Combo('V', ctrl: true)));
        Assert.True(ReservedShortcuts.IsReserved(Combo('C', ctrl: true)));
        Assert.True(ReservedShortcuts.IsReserved(Combo('S', ctrl: true)));
        Assert.True(ReservedShortcuts.IsReserved(Combo('Z', ctrl: true, shift: true)));
        Assert.True(ReservedShortcuts.IsReserved(Combo(Tab, ctrl: true)));
        Assert.True(ReservedShortcuts.IsReserved(Combo(Tab, alt: true)));
        Assert.True(ReservedShortcuts.IsReserved(Combo(F4, alt: true)));
        Assert.True(ReservedShortcuts.IsReserved(Combo(Space, alt: true)));
    }

    /// Windows uses nearly every Win shortcut: Explorer, clipboard history, screenshots.
    [Fact]
    public void WindowsKeyShortcutsAreReserved()
    {
        Assert.True(ReservedShortcuts.IsReserved(Combo('E', win: true)));
        Assert.True(ReservedShortcuts.IsReserved(Combo('V', win: true)));
        Assert.True(ReservedShortcuts.IsReserved(Combo('S', win: true, shift: true)));
        Assert.True(ReservedShortcuts.IsReserved(Combo('D', ctrl: true, win: true)));
        Assert.True(ReservedShortcuts.IsReserved(Combo(Left, ctrl: true, win: true)));
    }

    [Fact]
    public void OtherShortcutsStayFree()
    {
        Assert.False(ReservedShortcuts.IsReserved(Combo('V', ctrl: true, alt: true)));
        Assert.False(ReservedShortcuts.IsReserved(Combo('D', ctrl: true, alt: true)));
        Assert.False(ReservedShortcuts.IsReserved(Combo('D', ctrl: true)));
        Assert.False(ReservedShortcuts.IsReserved(Combo(F9)));
        Assert.False(ReservedShortcuts.IsReserved(Combo(Space, ctrl: true, alt: true)));
        Assert.False(ReservedShortcuts.IsReserved(Combo('E', ctrl: true, win: true)));
    }

    /// The defaults are modifier keys alone (Strg + Win and so on).
    [Fact]
    public void DefaultShortcutsAreFree()
    {
        foreach (var type in Enum.GetValues<WorkflowType>())
        {
            if (KeyCombo.DefaultFor(type) is { } combo) Assert.False(ReservedShortcuts.IsReserved(combo));
        }
    }
}
