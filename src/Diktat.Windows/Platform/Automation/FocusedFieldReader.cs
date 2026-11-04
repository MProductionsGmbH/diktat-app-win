using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace Diktat.Windows.Platform;

/// What Diktat can see of the text field a dictation goes into.
public sealed record FocusedField
{
    public int ProcessId { get; init; }
    /// A password field: nothing was read.
    public bool IsPassword { get; init; }
    /// Whether the field reported a cursor. Without one (ValuePattern only) the cursor is
    /// assumed at the end.
    public bool HasCaret { get; init; }
    public string TextBefore { get; init; } = "";
    public string SelectedText { get; init; } = "";
    public string TextAfter { get; init; } = "";

    /// The surrounding text for the AI modes.
    public string? AiContext
    {
        get
        {
            var parts = new[] { TextBefore, SelectedText, TextAfter }.Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
            return parts.Length == 0 ? null : string.Join("\n\n", parts);
        }
    }
}

/// Where the focus is, to check before pasting that it is still in the same field.
public sealed record FocusSpot(int ProcessId, int[]? RuntimeId, int ControlTypeId, System.Windows.Rect Bounds)
{
    /// The same field, also when the app rebuilt its automation element meanwhile: another
    /// field differs in type or place.
    public bool IsSameField(FocusSpot other)
    {
        if (ProcessId != other.ProcessId) return false;
        if (RuntimeId is not null && other.RuntimeId is not null && RuntimeId.SequenceEqual(other.RuntimeId)) return true;
        return ControlTypeId == other.ControlTypeId && Bounds == other.Bounds;
    }
}

/// Reads the focused text field through UI Automation (port of the Mac FocusedFieldReader).
/// Password fields are never read. Every call runs on a background MTA thread and gives up
/// after ~300 ms, so a hanging app cannot hold up a dictation. Diktat's own windows are skipped.
public static class FocusedFieldReader
{
    public const int BeforeLimit = 2_000;
    public const int AfterLimit = 600;

    /// Loads UI Automation once (the first call in a process costs ~100 ms). Optional.
    public static Task WarmupAsync() => UiaRunner.RunAsync<object>(() =>
    {
        _ = AutomationElement.RootElement;
        return null;
    }, TimeSpan.FromSeconds(2));

    /// Text around the cursor of the focused field, null when nothing readable has focus.
    public static Task<FocusedField?> ReadAsync(TimeSpan? timeout = null) => UiaRunner.RunAsync(Read, timeout);

    /// The focused element, for <see cref="FocusSpot.IsSameField"/> later. Null on timeout,
    /// for Diktat's own windows, or when nothing has focus.
    public static Task<FocusSpot?> CaptureFocusSpotAsync(TimeSpan? timeout = null) => UiaRunner.RunAsync(() =>
    {
        var element = AutomationElement.FocusedElement;
        if (element is null) return null;
        var current = element.Current;
        if (current.ProcessId == Environment.ProcessId) return null;
        return new FocusSpot(current.ProcessId, element.GetRuntimeId(), current.ControlType.Id, current.BoundingRectangle);
    }, timeout);

    private static FocusedField? Read()
    {
        var element = AutomationElement.FocusedElement;
        if (element is null) return null;
        var current = element.Current;
        if (current.ProcessId == Environment.ProcessId) return null;
        if (current.IsPassword) return new FocusedField { ProcessId = current.ProcessId, IsPassword = true };

        if (element.TryGetCurrentPattern(TextPattern.Pattern, out var textObject) && textObject is TextPattern text)
        {
            var field = ReadText(text, current.ProcessId);
            if (field is not null) return field;
        }

        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valueObject) && valueObject is ValuePattern value)
        {
            var all = value.Current.Value ?? "";
            return new FocusedField
            {
                ProcessId = current.ProcessId,
                HasCaret = false,
                TextBefore = all.Length > BeforeLimit ? all[^BeforeLimit..] : all,
            };
        }
        return null;
    }

    private static FocusedField? ReadText(TextPattern text, int processId)
    {
        var selections = text.GetSelection();
        if (selections is null || selections.Length == 0) return null;
        var selection = selections[0];

        // Before: collapse to the selection start, then extend the start backwards.
        var before = selection.Clone();
        before.MoveEndpointByRange(TextPatternRangeEndpoint.End, selection, TextPatternRangeEndpoint.Start);
        before.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -BeforeLimit);

        // After: collapse to the selection end, then extend the end forwards.
        var after = selection.Clone();
        after.MoveEndpointByRange(TextPatternRangeEndpoint.Start, selection, TextPatternRangeEndpoint.End);
        after.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, AfterLimit);

        return new FocusedField
        {
            ProcessId = processId,
            HasCaret = true,
            TextBefore = Limit(before.GetText(BeforeLimit + 16), BeforeLimit, fromEnd: true),
            SelectedText = Limit(selection.GetText(BeforeLimit), BeforeLimit, fromEnd: false),
            TextAfter = Limit(after.GetText(AfterLimit + 16), AfterLimit, fromEnd: false),
        };
    }

    private static string Limit(string? value, int limit, bool fromEnd)
    {
        value ??= "";
        if (value.Length <= limit) return value;
        return fromEnd ? value[^limit..] : value[..limit];
    }
}
