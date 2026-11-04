using Diktat.Windows.Platform;
using Diktat.Windows.Platform.Native;

namespace Diktat.Windows.Runtime;

/// "Wortgenau statt KI-Text": selects the text an AI mode just pasted (Shift+Left from the cursor)
/// and pastes what was said over it. Only when the field confirms the selection; otherwise the
/// selection is dropped again and the caller copies the transcript instead.
internal static class TranscriptReplacer
{
    /// Longer texts take too many key presses to select reliably.
    private const int MaximumSteps = 3_000;

    /// True when <paramref name="inserted"/> is selected in the target field now.
    public static async Task<bool> SelectInsertedAsync(PasteTarget target, string inserted)
    {
        int steps = DictationRules.CaretSteps(inserted);
        if (steps == 0 || steps > MaximumSteps) return false;
        if (!await BringToFrontAsync(target.Window)) return false;
        await KeyboardInput.WaitForModifiersReleasedAsync(TimeSpan.FromSeconds(1));

        var inputs = new List<Win32.INPUT> { KeyboardInput.Key(VK.LShift, up: false) };
        for (int i = 0; i < steps; i++)
        {
            inputs.Add(KeyboardInput.Key(0x25, up: false)); // VK_LEFT
            inputs.Add(KeyboardInput.Key(0x25, up: true));
        }
        inputs.Add(KeyboardInput.Key(VK.LShift, up: true));
        if (!KeyboardInput.Send(inputs)) return false;

        await Task.Delay(60);
        var field = await FocusedFieldReader.ReadAsync();
        if (field is { IsPassword: false } && field.ProcessId == target.ProcessId && SameText(field.SelectedText, inserted)) return true;

        // Not confirmed: put the cursor back where it was (the end of the selection).
        KeyboardInput.Send([KeyboardInput.Key(0x27, up: false), KeyboardInput.Key(0x27, up: true)]); // VK_RIGHT
        return false;
    }

    private static async Task<bool> BringToFrontAsync(IntPtr window)
    {
        if (!Win32.IsWindow(window) || Win32.IsIconic(window)) return false;
        if (Win32.GetAncestor(Win32.GetForegroundWindow(), Win32.GA_ROOT) == window) return true;
        Win32.SetForegroundWindow(window);
        var deadline = Environment.TickCount64 + 400;
        while (Win32.GetAncestor(Win32.GetForegroundWindow(), Win32.GA_ROOT) != window)
        {
            if (Environment.TickCount64 > deadline) return false;
            await Task.Delay(15);
        }
        return true;
    }

    /// Fields change line breaks (\n to \r\n) and may trim; the words must match.
    internal static bool SameText(string selected, string inserted) => Squeeze(selected) == Squeeze(inserted);

    private static string Squeeze(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
