using Diktat.Core.Settings;
using Diktat.Windows.Platform.Native;

namespace Diktat.Windows.Platform;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Ctrl = 1,
    Alt = 2,
    Shift = 4,
    Win = 8,
}

/// What the state machine asks its host to do. The host (the hook) executes these right away
/// on the hook thread, except the Raise* calls, which it queues to the event dispatcher.
internal interface IHotkeyHost
{
    void RaisePressed(WorkflowType type);
    void RaiseReleased(WorkflowType type);
    void RaiseCancelled();
    /// A registered combo is held but the decision is still open (more specific combo may follow).
    void RaisePotentialStart(WorkflowType type);
    void CaptureProgress(KeyCombo held);
    void CaptureCompleted(KeyCombo? combo);
    /// Injects mask key down + up.
    void SendMenuMask();
    /// Injects mask key down + up followed by the key-up of <paramref name="vk"/> (whose physical
    /// key-up the machine swallows), so the Start menu / menu bar sees a key in between.
    void SendMenuMaskThenKeyUp(int vk, int scanCode, bool extended);
    void StartDecisionTimer(int milliseconds);
    void StopDecisionTimer();
    /// Whether the key is down according to GetAsyncKeyState; used to repair state after missed
    /// key-ups (secure desktop, lock screen, UAC prompt).
    bool IsKeyDown(int vk);
}

/// The shortcut logic of <see cref="GlobalHotkeyService"/>, free of Win32 so it can be tested.
/// Not thread-safe: the hook thread is its only caller.
///
/// Rules:
/// * Combos are compared exactly: the held modifiers must equal the combo's modifiers.
/// * A modifier-only combo that is part of a larger registered combo (Ctrl+Win inside
///   Ctrl+Win+Alt) waits <see cref="DecisionDelayMs"/> or until a key goes up, so the larger one
///   can win. Unambiguous combos fire at once.
/// * Hold: the combo fires Pressed; releasing any of its keys fires Released. Toggle: the combo
///   fires Pressed, the next combo press fires Released.
/// * Escape while recording (or while <see cref="WorkflowBusy"/>) fires Cancelled and is swallowed.
/// * After a decision the keys still held are ignored until everything is released.
/// * Modifier key events always pass to the system (apart from the Win/Alt key-up trick).
///   Only the main key of a matched combo is swallowed. Other shortcuts (Win+E, Ctrl+Win+D) pass.
internal sealed class HotkeyStateMachine
{
    public const int DecisionDelayMs = 150;

    private readonly IHotkeyHost _host;

    private readonly record struct Binding(WorkflowType Type, HotkeyModifiers Mods, int? Key);

    private Binding[] _bindings = [];

    // Physical state (non-injected events only).
    private bool _lCtrl, _rCtrl, _lAlt, _rAlt, _lShift, _rShift, _lWin, _rWin;
    private readonly HashSet<int> _keysDown = new();
    private readonly HashSet<int> _swallowed = new();

    private Binding? _pending;
    /// The combo whose keys are still held after it fired.
    private Binding? _engaged;
    /// The engaged press started a recording (as opposed to stopping one in toggle mode).
    private bool _engagedStarted;
    /// Toggle mode: the workflow started by a press and not yet stopped.
    private WorkflowType? _toggledOn;
    /// Ignore all keys until every key is released.
    private bool _latched;

    private bool _suppressMenuWin, _maskSentWin, _suppressMenuAlt, _maskSentAlt;

    private CaptureState? _capture;

    private sealed class CaptureState
    {
        public HotkeyModifiers MaxMods;
    }

    public HotkeyStateMachine(IHotkeyHost host) => _host = host;

    public HotkeyMode Mode { get; set; } = HotkeyMode.Hold;

    /// The app is recording or processing a dictation: Escape is taken and cancels it.
    public bool WorkflowBusy { get; set; }

    public bool Suspended { get; private set; }

    public bool IsCapturing => _capture is not null;

    public WorkflowType? ToggledOn => _toggledOn;

    public HotkeyModifiers Held =>
        (_lCtrl || _rCtrl ? HotkeyModifiers.Ctrl : 0) |
        (_lAlt || _rAlt ? HotkeyModifiers.Alt : 0) |
        (_lShift || _rShift ? HotkeyModifiers.Shift : 0) |
        (_lWin || _rWin ? HotkeyModifiers.Win : 0);

    public static HotkeyModifiers ModsOf(KeyCombo combo) =>
        (combo.Ctrl ? HotkeyModifiers.Ctrl : 0) | (combo.Alt ? HotkeyModifiers.Alt : 0) |
        (combo.Shift ? HotkeyModifiers.Shift : 0) | (combo.Win ? HotkeyModifiers.Win : 0);

    public static KeyCombo ComboOf(HotkeyModifiers mods, int? key) => new()
    {
        Ctrl = mods.HasFlag(HotkeyModifiers.Ctrl),
        Alt = mods.HasFlag(HotkeyModifiers.Alt),
        Shift = mods.HasFlag(HotkeyModifiers.Shift),
        Win = mods.HasFlag(HotkeyModifiers.Win),
        Key = key,
    };

    public static bool IsModifierKey(int vk) => vk is VK.LControl or VK.RControl or VK.LMenu or VK.RMenu
        or VK.LShift or VK.RShift or VK.LWin or VK.RWin or VK.Control or VK.Menu or VK.Shift;

    public static bool IsFunctionKey(int vk) => vk is >= VK.F1 and <= VK.F24;

    /// Keys that never take part in a combo (lock keys toggle state, they are not shortcuts).
    private static bool IsIgnoredKey(int vk) => vk is VK.Capital or VK.NumLock or VK.Scroll or VK.MenuMask or 0;

    public void SetBindings(IEnumerable<KeyValuePair<WorkflowType, KeyCombo?>> combos)
    {
        var list = new List<Binding>();
        foreach (var (type, combo) in combos)
        {
            if (combo is null) continue;
            var mods = ModsOf(combo);
            if (combo.Key is int key)
            {
                if (IsModifierKey(key) || IsIgnoredKey(key)) continue;
                list.Add(new Binding(type, mods, key));
            }
            else
            {
                // A single modifier alone would fire with every normal shortcut.
                if (combo.ModifierCount < 2) continue;
                list.Add(new Binding(type, mods, null));
            }
        }
        _bindings = list.ToArray();
        // A pending decision may refer to a combo that is gone.
        CancelPending();
        if (_engaged is { } engaged && !_bindings.Contains(engaged)) _engaged = null;
    }

    public void SetSuspended(bool suspended)
    {
        if (Suspended == suspended) return;
        Suspended = suspended;
        CancelPending();
        _engaged = null;
        _latched = Held != HotkeyModifiers.None;
    }

    /// The orchestrator ended the recording on its own (error, length limit): the next toggle
    /// press starts a new one instead of stopping.
    public void ResetToggle() => _toggledOn = null;

    public void BeginCapture()
    {
        CancelPending();
        _engaged = null;
        _capture = new CaptureState { MaxMods = Held };
    }

    public void EndCapture(KeyCombo? result)
    {
        if (_capture is null) return;
        _capture = null;
        _latched = true;
        _host.CaptureCompleted(result);
    }

    // ---------------------------------------------------------------- events

    /// A physical (non-injected) key event. Returns true to swallow it.
    public bool OnKey(int vk, int scanCode, bool extended, bool down)
    {
        vk = NormalizeVk(vk);
        if (IsIgnoredKey(vk)) return false;
        RepairStaleState(vk);

        bool swallow = IsModifierKey(vk) ? OnModifier(vk, scanCode, extended, down) : OnMainKey(vk, down);
        if (down && !swallow && !IsModifierKey(vk))
        {
            // A key reached Windows between Win/Alt down and up: the menu will not open anyway.
            if (_lWin || _rWin) _maskSentWin = true;
            if (_lAlt || _rAlt) _maskSentAlt = true;
        }

        if (Held == HotkeyModifiers.None && _keysDown.Count == 0)
        {
            _latched = false;
            if (_engaged is not null && Mode == HotkeyMode.Toggle) _engaged = null;
        }
        return swallow;
    }

    public void OnDecisionTimer()
    {
        _host.StopDecisionTimer();
        if (_pending is not { } pending) return;
        _pending = null;
        RepairStaleState(0);
        if (Held == pending.Mods && UnswallowedKeysDown() == 0 && !_latched && _engaged is null)
        {
            Activate(pending);
        }
    }

    private static int NormalizeVk(int vk) => vk switch
    {
        VK.Control => VK.LControl,
        VK.Menu => VK.LMenu,
        VK.Shift => VK.LShift,
        _ => vk,
    };

    private bool OnModifier(int vk, int scanCode, bool extended, bool down)
    {
        bool wasWin = _lWin || _rWin, wasAlt = _lAlt || _rAlt;
        ref bool side = ref SideFlag(vk);
        if (down)
        {
            if (side) return false; // auto-repeat of a held modifier
            side = true;
            if (!wasWin && (vk is VK.LWin or VK.RWin)) { _suppressMenuWin = false; _maskSentWin = false; }
            if (!wasAlt && (vk is VK.LMenu or VK.RMenu)) { _suppressMenuAlt = false; _maskSentAlt = false; }

            if (_capture is not null)
            {
                _capture.MaxMods |= Held;
                if (Held.HasFlag(HotkeyModifiers.Win)) _suppressMenuWin = true;
                if (Held.HasFlag(HotkeyModifiers.Alt)) _suppressMenuAlt = true;
                _host.CaptureProgress(ComboOf(Held, null));
                return false;
            }
            if (!Suspended) EvaluateModifierChange();
            return false;
        }

        // Key up.
        if (!side) return MaskOnRelease(vk, scanCode, extended); // up without a seen down
        var before = Held;
        side = false;

        if (_capture is not null)
        {
            // Modifiers alone, at least two, released without a main key: that is the combo.
            var mods = _capture.MaxMods;
            int count = CountBits(mods);
            if (count >= 2) EndCapture(ComboOf(mods, null));
            else if (Held == HotkeyModifiers.None) _capture.MaxMods = HotkeyModifiers.None;
            else _host.CaptureProgress(ComboOf(Held, null));
            return MaskOnRelease(vk, scanCode, extended);
        }

        if (!Suspended)
        {
            if (_pending is { } pending)
            {
                // A key went up before the decision: decide now.
                CancelPending();
                if (Mode == HotkeyMode.Toggle && before == pending.Mods && !_latched && _engaged is null)
                {
                    Activate(pending);
                }
                // Hold: a tap this short is no dictation. Either way the rest of the press is ignored.
                _engaged = null;
                _latched = true;
            }
            else if (_engaged is { } engaged && engaged.Mods.HasFlag(ModFlagOf(vk)) && !Held.HasFlag(ModFlagOf(vk)))
            {
                EngagedPartReleased(engaged);
            }
            else if (!_latched && _engaged is null)
            {
                EvaluateModifierChange();
            }
        }

        return MaskOnRelease(vk, scanCode, extended);
    }

    /// On the last Win (or Alt) key-up: if one of our combos was recognised during this press
    /// and no key event has gone between down and up yet, swallow the up and inject mask + up.
    private bool MaskOnRelease(int vk, int scanCode, bool extended)
    {
        if (vk is VK.LWin or VK.RWin && !(_lWin || _rWin))
        {
            bool needed = _suppressMenuWin && !_maskSentWin;
            _suppressMenuWin = false;
            _maskSentWin = false;
            if (needed)
            {
                _host.SendMenuMaskThenKeyUp(vk, scanCode, extended);
                if (_lAlt || _rAlt) _maskSentAlt = true;
                return true;
            }
        }
        else if (vk is VK.LMenu or VK.RMenu && !(_lAlt || _rAlt))
        {
            bool needed = _suppressMenuAlt && !_maskSentAlt;
            _suppressMenuAlt = false;
            _maskSentAlt = false;
            if (needed)
            {
                _host.SendMenuMaskThenKeyUp(vk, scanCode, extended);
                if (_lWin || _rWin) _maskSentWin = true;
                return true;
            }
        }
        return false;
    }

    private bool OnMainKey(int vk, bool down)
    {
        if (!down)
        {
            _keysDown.Remove(vk);
            bool wasSwallowed = _swallowed.Remove(vk);
            if (_capture is null && !Suspended && _engaged is { } engaged && engaged.Key == vk)
            {
                EngagedPartReleased(engaged);
            }
            return wasSwallowed;
        }

        bool repeat = !_keysDown.Add(vk);
        if (_swallowed.Contains(vk)) return true; // auto-repeat of a key we took

        if (_capture is not null)
        {
            if (repeat) return true;
            var held = Held;
            if (vk == VK.Escape)
            {
                _swallowed.Add(vk);
                EndCapture(null);
                return true;
            }
            // Shift alone (or nothing) plus a normal key would collide with typing.
            if ((held & ~HotkeyModifiers.Shift) == HotkeyModifiers.None && !IsFunctionKey(vk))
            {
                _swallowed.Add(vk);
                return true;
            }
            _swallowed.Add(vk);
            SendMaskIfNeeded();
            EndCapture(ComboOf(held, vk));
            return true;
        }

        if (Suspended || repeat) return false;

        if (vk == VK.Escape)
        {
            if (_pending is not null)
            {
                CancelPending();
                _latched = true;
                return false;
            }
            if (_engaged is not null || _toggledOn is not null || WorkflowBusy)
            {
                _engaged = null;
                _toggledOn = null;
                _latched = Held != HotkeyModifiers.None || UnswallowedKeysDown() > 1;
                _swallowed.Add(vk);
                SendMaskIfNeeded();
                _host.RaiseCancelled();
                return true;
            }
            return false;
        }

        if (_engaged is { } current)
        {
            // Another key while a modifier-only combo that started a recording is held: the user
            // meant a Windows shortcut such as Ctrl+Win+D. Give the key to Windows, drop the recording.
            if (current.Key is null && _engagedStarted)
            {
                _engaged = null;
                _toggledOn = null;
                _latched = true;
                _host.RaiseCancelled();
            }
            return false;
        }

        if (_latched) return false;

        CancelPending();
        var mods = Held;
        foreach (var binding in _bindings)
        {
            if (binding.Key == vk && binding.Mods == mods)
            {
                _swallowed.Add(vk);
                Activate(binding);
                return true;
            }
        }
        return false;
    }

    private void EvaluateModifierChange()
    {
        CancelPending();
        if (_engaged is not null || _latched) return;
        if (UnswallowedKeysDown() > 0) return;

        var mods = Held;
        if (mods == HotkeyModifiers.None) return;
        Binding? exact = null;
        foreach (var binding in _bindings)
        {
            if (binding.Key is null && binding.Mods == mods) { exact = binding; break; }
        }
        if (exact is not { } match) return;

        // Recognised one of ours: the Start menu / menu bar must not open on release.
        if (mods.HasFlag(HotkeyModifiers.Win)) _suppressMenuWin = true;
        if (mods.HasFlag(HotkeyModifiers.Alt)) _suppressMenuAlt = true;

        bool ambiguous = false;
        foreach (var binding in _bindings)
        {
            if (binding == match) continue;
            // A larger modifier set, or the same modifiers plus a main key.
            if ((binding.Mods & mods) == mods && (binding.Mods != mods || binding.Key is not null))
            {
                ambiguous = true;
                break;
            }
        }

        // Stopping a toggled recording needs no specificity: any of our combos stops it.
        if (!ambiguous || _toggledOn is not null)
        {
            Activate(match);
            return;
        }

        _pending = match;
        _host.RaisePotentialStart(match.Type);
        _host.StartDecisionTimer(DecisionDelayMs);
    }

    private void Activate(Binding binding)
    {
        CancelPending();
        SendMaskIfNeeded();

        if (Mode == HotkeyMode.Hold)
        {
            _engaged = binding;
            _engagedStarted = true;
            _host.RaisePressed(binding.Type);
            return;
        }

        _engaged = binding;
        if (_toggledOn is { } running)
        {
            _toggledOn = null;
            _engagedStarted = false;
            _host.RaiseReleased(running);
        }
        else
        {
            _toggledOn = binding.Type;
            _engagedStarted = true;
            _host.RaisePressed(binding.Type);
        }
    }

    private void EngagedPartReleased(Binding engaged)
    {
        _engaged = null;
        _latched = true;
        if (Mode == HotkeyMode.Hold && _engagedStarted) _host.RaiseReleased(engaged.Type);
        _engagedStarted = false;
    }

    private void SendMaskIfNeeded()
    {
        bool win = _lWin || _rWin, alt = _lAlt || _rAlt;
        if (!win && !alt) return;
        _host.SendMenuMask();
        if (win) { _suppressMenuWin = true; _maskSentWin = true; }
        if (alt) { _suppressMenuAlt = true; _maskSentAlt = true; }
    }

    private void CancelPending()
    {
        if (_pending is null) return;
        _pending = null;
        _host.StopDecisionTimer();
    }

    private int UnswallowedKeysDown()
    {
        int count = 0;
        foreach (var key in _keysDown)
        {
            if (!_swallowed.Contains(key)) count++;
        }
        return count;
    }

    /// Clears keys we believe are down but Windows says are up (their key-up went to the secure
    /// desktop, or the hook was not called). Skips <paramref name="currentVk"/> (its async state
    /// is not updated yet) and keys we swallowed (their async state never went down).
    private void RepairStaleState(int currentVk)
    {
        var before = Held;
        Repair(ref _lCtrl, VK.LControl, currentVk);
        Repair(ref _rCtrl, VK.RControl, currentVk);
        Repair(ref _lAlt, VK.LMenu, currentVk);
        Repair(ref _rAlt, VK.RMenu, currentVk);
        Repair(ref _lShift, VK.LShift, currentVk);
        Repair(ref _rShift, VK.RShift, currentVk);
        Repair(ref _lWin, VK.LWin, currentVk);
        Repair(ref _rWin, VK.RWin, currentVk);
        if (_keysDown.Count > 0)
        {
            _keysDown.RemoveWhere(key => key != currentVk && !_swallowed.Contains(key) && !_host.IsKeyDown(key));
        }

        var after = Held;
        if (after == before) return;
        if (!after.HasFlag(HotkeyModifiers.Win)) { _suppressMenuWin = false; _maskSentWin = false; }
        if (!after.HasFlag(HotkeyModifiers.Alt)) { _suppressMenuAlt = false; _maskSentAlt = false; }
        if (_pending is { } pending && pending.Mods != after) CancelPending();
        if (_engaged is { } engaged && (engaged.Mods & after) != engaged.Mods) EngagedPartReleased(engaged);
    }

    private void Repair(ref bool flag, int vk, int currentVk)
    {
        if (flag && vk != currentVk && !_host.IsKeyDown(vk)) flag = false;
    }

    private ref bool SideFlag(int vk)
    {
        switch (vk)
        {
            case VK.LControl: return ref _lCtrl;
            case VK.RControl: return ref _rCtrl;
            case VK.LMenu: return ref _lAlt;
            case VK.RMenu: return ref _rAlt;
            case VK.LShift: return ref _lShift;
            case VK.RShift: return ref _rShift;
            case VK.LWin: return ref _lWin;
            default: return ref _rWin;
        }
    }

    private static HotkeyModifiers ModFlagOf(int vk) => vk switch
    {
        VK.LControl or VK.RControl => HotkeyModifiers.Ctrl,
        VK.LMenu or VK.RMenu => HotkeyModifiers.Alt,
        VK.LShift or VK.RShift => HotkeyModifiers.Shift,
        VK.LWin or VK.RWin => HotkeyModifiers.Win,
        _ => HotkeyModifiers.None,
    };

    private static int CountBits(HotkeyModifiers mods)
    {
        int value = (int)mods, count = 0;
        while (value != 0) { count += value & 1; value >>= 1; }
        return count;
    }
}
