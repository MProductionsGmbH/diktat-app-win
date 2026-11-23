using Diktat.Core.Settings;
using Diktat.Windows.Platform;
using Diktat.Windows.Platform.Native;

namespace Diktat.Windows.Tests;

public class HotkeyStateMachineTests
{
    private sealed class FakeHost : IHotkeyHost
    {
        public int Pressed, Released, Cancelled;
        public void RaisePressed(WorkflowType type) => Pressed++;
        public void RaiseReleased(WorkflowType type) => Released++;
        public void RaiseCancelled() => Cancelled++;
        public void RaisePotentialStart(WorkflowType type) { }
        public void CaptureProgress(KeyCombo held) { }
        public void CaptureCompleted(KeyCombo? combo) { }
        public void SendMenuMask() { }
        public void SendMenuMaskThenKeyUp(int vk, int scanCode, bool extended) { }
        public void StartDecisionTimer(int milliseconds) { }
        public void StopDecisionTimer() { }
        public bool IsKeyDown(int vk) => true;
    }

    private static (HotkeyStateMachine Machine, FakeHost Host) Make()
    {
        var host = new FakeHost();
        var machine = new HotkeyStateMachine(host);
        machine.SetBindings([new(WorkflowType.TapTalk, new KeyCombo { Ctrl = true, Win = true })]);
        return (machine, host);
    }

    private static bool Key(HotkeyStateMachine machine, int vk, bool down) => machine.OnKey(vk, 0, false, down);

    /// A held Escape repeats; the repeat must not cancel the dictation that runs after the first
    /// press ended the previous one.
    [Fact]
    public void HeldEscapeCancelsOnlyOnce()
    {
        var (machine, host) = Make();
        machine.WorkflowBusy = true;
        Assert.True(Key(machine, VK.Escape, down: true));
        Assert.Equal(1, host.Cancelled);

        // A new dictation, started from the tray panel while Escape is still held.
        machine.WorkflowBusy = true;
        for (var i = 0; i < 5; i++) Assert.True(Key(machine, VK.Escape, down: true));
        Assert.Equal(1, host.Cancelled);

        Assert.True(Key(machine, VK.Escape, down: false));
        Assert.True(Key(machine, VK.Escape, down: true));
        Assert.Equal(2, host.Cancelled);
    }

    /// Escape held from before the dictation passes to the program in front and stops nothing.
    [Fact]
    public void EscapeHeldBeforeTheDictationStopsNothing()
    {
        var (machine, host) = Make();
        Assert.False(Key(machine, VK.Escape, down: true));
        machine.WorkflowBusy = true;
        Assert.False(Key(machine, VK.Escape, down: true));
        Assert.Equal(0, host.Cancelled);
    }

    [Fact]
    public void EscapeCancelsAHeldRecording()
    {
        var (machine, host) = Make();
        Key(machine, VK.LControl, down: true);
        Key(machine, VK.LWin, down: true);
        Assert.Equal(1, host.Pressed);
        Assert.True(Key(machine, VK.Escape, down: true));
        Assert.True(Key(machine, VK.Escape, down: true));
        Assert.Equal(1, host.Cancelled);
    }
}
