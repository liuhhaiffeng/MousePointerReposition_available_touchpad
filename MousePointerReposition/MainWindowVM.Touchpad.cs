using MousePointerReposition.Helper;
using System;
using System.Windows.Interop;

namespace MousePointerReposition
{
    // ============================================================================
    // TOUCHPAD EXPERIMENT (isolated partial class)
    //
    // Everything in this file belongs to the experimental touchpad / foreground-
    // hook feature. See docs/touchpad-app-switch.md:
    //   - v2.0 control experiment: the foreground event hook path crashes on
    //     Win11 + WPF + multi-monitor ~9s after a gesture (0xC000041D / native AV
    //     inside WPF's Dispatcher.PushFrame). The keyboard paths are stable.
    //   - "共用路径污染事件": a guard placed inside the shared TryMouseRepositioning
    //     broke Alt+Tab on negative-coordinate monitors even with the switch OFF.
    //
    // CONSTRAINTS — do not break these when working on the experiment:
    //   1. Nothing in this file may execute while EnableTouchpadAppSwitch == false.
    //      The switch is consulted at exactly two places: hook registration
    //      (RegisterForegroundEventHooks) and callback entry (ForegroundEventCallback,
    //      re-checked after the dispatcher marshal).
    //   2. Do NOT move any of this logic into the shared methods of MainWindowVM.cs
    //      (TryMouseRepositioning / ExecuteMousePositioning / Trigger* / Key*Hook).
    //      If the experiment needs different behavior, keep the difference HERE.
    //   3. The single permitted touchpoint in the shared pipeline is the guard
    //      call inside TryMouseRepositioning, which delegates to
    //      ShouldSkipMoveForForegroundHookPath(). That guard is gated by the
    //      reposition TRIGGER (path), never by global state: keyboard-initiated
    //      moves always pass through exactly as on master, even when the
    //      experiment switch is ON.
    //   4. Isolation first, consolidation later: do not "clean up" or merge this
    //      file's logic into the shared pipeline until the experiment has passed
    //      its stability criteria (see docs: 平台风险特性开发流程).
    // ============================================================================
    public partial class MainWindowVM
    {
        #region experiment state

        /// <summary>
        /// Which path initiated the current repositioning sequence. Set by the
        /// Trigger* methods; read by the experiment guard. Keyboard paths always
        /// use <see cref="RepositionTrigger.Keyboard"/>.
        /// </summary>
        private enum RepositionTrigger
        {
            Keyboard,
            ForegroundHook
        }

        private bool? enableTouchpadAppSwitch;

        private System.Collections.Generic.List<Vanara.PInvoke.User32.HWINEVENTHOOK> ForegroundEventHooks { get; set; }

        private IntPtr LastForegroundHwnd { get; set; }

        private RepositionTrigger ActiveTrigger { get; set; } = RepositionTrigger.Keyboard;

        /// <summary>
        /// Experiment stage captured at hook registration
        /// (see SettingsHelper.TouchpadExperimentStage). Changing the registry
        /// value requires an app restart to take effect.
        /// </summary>
        private int ExperimentStage { get; set; } = 1;

        // CRITICAL: the WinEventProc delegate must be kept alive for the lifetime of
        // the hooks. Passing the method group to SetWinEventHook directly creates an
        // UNROOTED delegate that the GC can collect at any time; the next Win32 event
        // then jumps into a freed thunk -> native AccessViolation -> 0xC000041D
        // (STATUS_FATAL_USER_CALLBACK_EXCEPTION) seconds later. This unrooted-delegate
        // pattern is the prime suspect for the delayed crashes recorded in
        // docs/touchpad-app-switch.md (v2.0): it explains the 9-14s delay (needs a GC
        // cycle + a subsequent event), why try-catch / DispatcherUnhandledException /
        // e.Handled could not help (the AV is not inside our code), and why the
        // keyboard path never crashed (FMUtils roots its hook delegate properly).
        private Vanara.PInvoke.User32.WinEventProc ForegroundEventProcDelegate; // field: keeps the delegate alive against GC

        // Click-suppression (UX): a foreground change CAUSED BY a mouse/touchpad
        // button click must not move the cursor — it is already exactly where the
        // user put it. Covers: clicking a window's close/minimize button (the
        // window vanishes, focus falls to the next window, and without this guard
        // the cursor would jump to that window's center), taskbar icon clicks,
        // and any other click-driven activation. Touchpad three-finger SWIPES
        // generate no button events, so gesture switches are unaffected.
        // This hook is a passive observer: it only timestamps button-downs and
        // always calls CallNextHookEx. Registered/unregistered together with the
        // foreground event hooks (same switch gate).
        private const int MouseClickSuppressMs = 500; // click -> foreground-change latency budget
        private Vanara.PInvoke.User32.SafeHHOOK MouseButtonHook;
        private Vanara.PInvoke.User32.HookProc MouseButtonHookProcDelegate; // field: keep alive against GC (same unrooted-delegate lesson as WinEventProc)
        private System.DateTime LastMouseButtonDownUtc; // UI thread only (LL hook callbacks are delivered on the installing thread's message loop)
        private int LastMouseButtonMsg; // WM_*BUTTONDOWN identity of the last press — recorded (not logged) so suppression decisions can report their evidence

        // Keyboard-activity suppression (UX): foreground changes caused by keyboard
        // actions we do NOT handle as triggers (Win key opening Start, Win+E
        // launching Explorer, etc.) must not move the cursor either. Together with
        // click-suppression, the foreground path reacts only to switches that
        // produced no recent keyboard AND no recent button input — which,
        // empirically, means touchpad swipe gestures (they inject neither; see
        // docs/touchpad-app-switch.md). Passive observer: timestamp only, always
        // CallNextHookEx, no per-keystroke logging (that lesson shipped and was
        // removed in 0d98967).
        private const int KeyboardSuppressMs = 1000; // key -> new-window-foreground latency budget
        private Vanara.PInvoke.User32.SafeHHOOK KeyboardActivityHook;
        private Vanara.PInvoke.User32.HookProc KeyboardActivityHookProcDelegate; // field: keep alive against GC
        private System.DateTime LastKeyboardActivityUtc; // UI thread only
        private uint LastKeyboardVk; // vkCode of the last key event — same decision-evidence purpose

        /// <summary>Formats a UTC timestamp as an age string for decision-point diagnostics.</summary>
        private static string FormatAgeUtc(System.DateTime utc)
        {
            if (utc == default(System.DateTime)) return "never";
            return ((int)(System.DateTime.UtcNow - utc).TotalMilliseconds) + "ms";
        }

        #endregion experiment state

        #region settings property

        /// <summary>
        /// Enable mouse cursor repositioning when the foreground window changes
        /// via touchpad gestures or other system-level foreground transitions
        /// (Task View, snap assist, etc.).
        /// Default: OFF — a missing registry value reads as 0/false.
        /// </summary>
        public bool EnableTouchpadAppSwitch
        {
            get
            {
                if (!enableTouchpadAppSwitch.HasValue)
                    enableTouchpadAppSwitch = SettingsHelper.Instance.EnableTouchpadAppSwitch;
                return enableTouchpadAppSwitch ?? false; // default OFF
            }
            set
            {
                enableTouchpadAppSwitch = value;
                SettingsHelper.Instance.EnableTouchpadAppSwitch = value;
                OnPropertyChanged(nameof(EnableTouchpadAppSwitch));
            }
        }

        #endregion settings property

        #region hook lifecycle (mount / unmount — the only switch-gated entry points)

        /// <summary>
        /// Registers the foreground event hooks when the experiment switch is ON.
        ///   - EVENT_SYSTEM_FOREGROUND       : standard foreground change (Alt+Tab, snap, etc.)
        ///   - EVENT_SYSTEM_SWITCHSTART..END : switcher UI lifecycle (covers Win11 three-finger
        ///                                     gesture which may not always fire FOREGROUND)
        /// No-op when the switch is OFF. Called once from OnLoaded.
        /// </summary>
        private void RegisterForegroundEventHooks()
        {
            var stage = SettingsHelper.Instance.TouchpadExperimentStage;
            // 0/unset → Full: the feature works when the user turns the toggle ON.
            // Stages 1-4 remain as debugging instruments (opt-in via registry).
            if (stage < 1 || stage > 5) stage = 5;
            ExperimentStage = stage;
            App.logger.Information($"Foreground event hook init: EnableTouchpadAppSwitch={EnableTouchpadAppSwitch}, ExperimentStage={ExperimentStage}");
            ForegroundEventHooks = new System.Collections.Generic.List<Vanara.PInvoke.User32.HWINEVENTHOOK>();
            if (EnableTouchpadAppSwitch)
            {
                // root the callback delegate BEFORE registering (see field comment)
                ForegroundEventProcDelegate = ForegroundEventCallback;

                // EVENT_SYSTEM_FOREGROUND only. The SWITCHSTART..END range was dropped:
                // Win11 never emits those events (zero occurrences across all captured
                // logs) and gestures reliably fire FOREGROUND anyway.
                var hookFront = Vanara.PInvoke.User32.SetWinEventHook(
                    Vanara.PInvoke.User32.EventConstants.EVENT_SYSTEM_FOREGROUND,
                    Vanara.PInvoke.User32.EventConstants.EVENT_SYSTEM_FOREGROUND,
                    Vanara.PInvoke.HINSTANCE.NULL,
                    ForegroundEventProcDelegate,
                    0,
                    0,
                    Vanara.PInvoke.User32.WINEVENT.WINEVENT_OUTOFCONTEXT);
                if (hookFront.IsNull)
                    App.logger.Warning("Failed to register EVENT_SYSTEM_FOREGROUND hook");
                else
                    ForegroundEventHooks.Add(hookFront);

                App.logger.Debug($"Foreground event hooks registered: {ForegroundEventHooks.Count}/1");

                // passive mouse-button observer (click-suppression, see field comment)
                MouseButtonHookProcDelegate = MouseButtonHookProc;
                MouseButtonHook = Vanara.PInvoke.User32.SetWindowsHookEx(
                    Vanara.PInvoke.User32.HookType.WH_MOUSE_LL,
                    MouseButtonHookProcDelegate,
                    Vanara.PInvoke.HINSTANCE.NULL,
                    0);
                if (MouseButtonHook.IsNull)
                    App.logger.Warning("Mouse button hook failed to register; click-caused switches will not be suppressed");
                else
                    App.logger.Debug("Mouse button hook registered (click-suppression)");

                // passive keyboard-activity observer (keyboard suppression, see field comment)
                KeyboardActivityHookProcDelegate = KeyboardActivityHookProc;
                KeyboardActivityHook = Vanara.PInvoke.User32.SetWindowsHookEx(
                    Vanara.PInvoke.User32.HookType.WH_KEYBOARD_LL,
                    KeyboardActivityHookProcDelegate,
                    Vanara.PInvoke.HINSTANCE.NULL,
                    0);
                if (KeyboardActivityHook.IsNull)
                    App.logger.Warning("Keyboard activity hook failed to register; keyboard-caused switches will not be suppressed");
                else
                    App.logger.Debug("Keyboard activity hook registered (keyboard-suppression)");
            }
            else
            {
                App.logger.Debug("Foreground event hook skipped (disabled by setting)");
            }
        }

        /// <summary>
        /// Unhooks the foreground event hooks. No-op when the experiment never
        /// registered any (switch OFF). Called once from OnClosing.
        /// </summary>
        private void UnhookForegroundEventHooks()
        {
            if (ForegroundEventHooks != null)
            {
                foreach (var hook in ForegroundEventHooks)
                {
                    if (!hook.IsNull)
                    {
                        App.logger.Debug("UnhookWinEvent");
                        Vanara.PInvoke.User32.UnhookWinEvent(hook);
                    }
                }
                ForegroundEventHooks.Clear();
            }

            if (MouseButtonHook != null && !MouseButtonHook.IsNull)
            {
                App.logger.Debug("UnhookWindowsHookEx (mouse button hook)");
                Vanara.PInvoke.User32.UnhookWindowsHookEx(MouseButtonHook); // SafeHHOOK → HHOOK implicit
                MouseButtonHook = null;
            }

            if (KeyboardActivityHook != null && !KeyboardActivityHook.IsNull)
            {
                App.logger.Debug("UnhookWindowsHookEx (keyboard activity hook)");
                Vanara.PInvoke.User32.UnhookWindowsHookEx(KeyboardActivityHook);
                KeyboardActivityHook = null;
            }
        }

        #endregion hook lifecycle

        #region hook callback

        /// <summary>
        /// Passive WH_MOUSE_LL callback: timestamps physical button-downs so the
        /// foreground callback can tell click-caused switches from gesture-caused
        /// ones. Never interferes with input — always CallNextHookEx.
        /// </summary>
        private IntPtr MouseButtonHookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            const int WM_LBUTTONDOWN = 0x0201;
            const int WM_RBUTTONDOWN = 0x0204;
            const int WM_MBUTTONDOWN = 0x0207;
            const int WM_XBUTTONDOWN = 0x020B;
            if (nCode >= 0)
            {
                int msg = unchecked((int)wParam.ToInt64());
                if (msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN || msg == WM_MBUTTONDOWN || msg == WM_XBUTTONDOWN)
                {
                    LastMouseButtonDownUtc = System.DateTime.UtcNow;
                    LastMouseButtonMsg = msg;
                }
            }
            // hhk of CallNextHookEx is ignored by the system; pass a null handle.
            return Vanara.PInvoke.User32.CallNextHookEx(new Vanara.PInvoke.User32.HHOOK(IntPtr.Zero), nCode, wParam, lParam);
        }

        /// <summary>
        /// Passive WH_KEYBOARD_LL callback: timestamps keyboard activity so the
        /// foreground callback can tell keyboard-caused switches (Win key,
        /// Win+E, ...) from gesture-caused ones. Never interferes with input.
        /// </summary>
        private IntPtr KeyboardActivityHookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var kb = System.Runtime.InteropServices.Marshal.PtrToStructure<Vanara.PInvoke.User32.KBDLLHOOKSTRUCT>(lParam);
                LastKeyboardVk = kb.vkCode;
                LastKeyboardActivityUtc = System.DateTime.UtcNow; // keydown and keyup both count for recency
            }
            return Vanara.PInvoke.User32.CallNextHookEx(new Vanara.PInvoke.User32.HHOOK(IntPtr.Zero), nCode, wParam, lParam);
        }

        /// <summary>
        /// Win32 event hook callback (fires on the hook thread, not the UI thread).
        /// Filters out self-window and dedupes rapid foreground churn, then
        /// marshals the trigger into the WPF UI thread.
        /// Only reachable when the experiment switch is ON (hooks are only
        /// registered then); re-checked after the dispatcher marshal.
        /// </summary>
        private void ForegroundEventCallback(
            Vanara.PInvoke.User32.HWINEVENTHOOK hWinEventHook,
            uint eventId,
            Vanara.PInvoke.HWND hwnd,
            int idObject,
            int idChild,
            uint idEventThread,
            uint dwmsEventTime)
        {
            App.logger.Debug($"ForegroundEventCallback: eventId=0x{eventId:X4} hwnd=0x{(hwnd.IsNull ? 0L : ((IntPtr)hwnd).ToInt64()):X}");

            // Note: EVENT_SYSTEM_SWITCHSTART/SWITCHEND are never emitted by Win11 (zero
            // occurrences across all captured logs) and we no longer hook that range;
            // every event arriving here is EVENT_SYSTEM_FOREGROUND.

            // filter self window (only meaningful when hwnd is valid)
            if (!hwnd.IsNull)
            {
                var mainWindow = System.Windows.Application.Current != null
                    ? System.Windows.Application.Current.MainWindow
                    : null;
                if (mainWindow != null)
                {
                    var selfHwnd = new WindowInteropHelper(mainWindow).Handle;
                    if ((IntPtr)hwnd == selfHwnd)
                    {
                        App.logger.Debug("ForegroundEventCallback: self window, skip");
                        return;
                    }
                }

                // dedupe rapid foreground churn on the SAME hwnd (e.g. spurious refires)
                var newHwnd = (IntPtr)hwnd;
                if (newHwnd == LastForegroundHwnd)
                {
                    return;
                }
                LastForegroundHwnd = newHwnd;
            }

            // Stage 1 (ObserveOnly): log the full event and stop — no dispatcher marshal.
            // Lets us compare the event stream of physical Alt+Tab vs touchpad gesture
            // without touching the WPF pipeline at all.
            if (ExperimentStage <= 1)
            {
                App.logger.Information($"[Stage1-Observe] eventId=0x{eventId:X4} hwnd=0x{(hwnd.IsNull ? 0L : ((IntPtr)hwnd).ToInt64()):X} idObject={idObject} idChild={idChild} thread={idEventThread} time={dwmsEventTime}");
                return;
            }

            // marshal to UI thread
            if (System.Windows.Application.Current == null)
                return;
            System.Windows.Application.Current.Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    // re-check setting after marshaling (user may have toggled off)
                    if (!EnableTouchpadAppSwitch)
                    {
                        App.logger.Debug("ForegroundEvent: skipped (disabled after marshal)");
                        return;
                    }

                    // Double-trigger dedupe: a foreground change caused by a keyboard shortcut
                    // we already handle (Alt+Tab / Win+arrows) arrives here only ~ms after the
                    // keyboard path started its own repositioning sequence. Let the keyboard
                    // path finish with master-exact behavior instead of hijacking the sequence.
                    // NOTE: deliberately does NOT consult IsManualMousePositioningTriggered —
                    // that flag's lifecycle is keyboard-Ctrl-only and previously leaked when
                    // the foreground path set it, causing a permanent dedupe lockout
                    // (gesture → flag stuck true → Win+arrow → trigger=Keyboard → all later
                    // gestures skipped forever). IsMousePositioningTriggered has a reliable
                    // keyboard-only lifecycle (set in TriggerMousePositioning, cleared in
                    // ExecuteMousePositioning) and covers the keydown→keyup window; the timer
                    // check covers the post-keyup retry window. 2xCtrl never changes the
                    // foreground window, so it needs no dedupe coverage.
                    if (ActiveTrigger == RepositionTrigger.Keyboard
                        && (RepositioningTimer.Enabled || IsMousePositioningTriggered))
                    {
                        App.logger.Debug("ForegroundEvent: skipped (keyboard-path repositioning in flight)");
                        return;
                    }

                    // Click-suppression: the foreground change was caused by a recent
                    // mouse/touchpad button click (window close/minimize button,
                    // taskbar icon, clicking another window) — the cursor is already
                    // where the user put it; do not move it. Gesture switches produce
                    // no button events and are unaffected. Known trade-off: a gesture
                    // within MouseClickSuppressMs after a click is also skipped
                    // (rare; self-heals on the next gesture).
                    if (MouseButtonHook != null && !MouseButtonHook.IsNull
                        && (System.DateTime.UtcNow - LastMouseButtonDownUtc).TotalMilliseconds < MouseClickSuppressMs)
                    {
                        App.logger.Debug($"ForegroundEvent: skipped (recent mouse click: msg=0x{LastMouseButtonMsg:X4} {FormatAgeUtc(LastMouseButtonDownUtc)} ago < {MouseClickSuppressMs}ms)");
                        return;
                    }

                    // Keyboard-activity suppression: the foreground change follows recent
                    // keyboard input we do not handle as a trigger (Win key opening
                    // Start, Win+E launching Explorer, launching apps by typing, ...).
                    // Alt+Tab / Win+arrows were already deduped above. Net effect:
                    // the foreground path only reacts to gesture-caused switches.
                    if (KeyboardActivityHook != null && !KeyboardActivityHook.IsNull
                        && (System.DateTime.UtcNow - LastKeyboardActivityUtc).TotalMilliseconds < KeyboardSuppressMs)
                    {
                        App.logger.Debug($"ForegroundEvent: skipped (recent keyboard input: vk=0x{LastKeyboardVk:X2} {FormatAgeUtc(LastKeyboardActivityUtc)} ago < {KeyboardSuppressMs}ms)");
                        return;
                    }
                    // Stage 2 (MarshalOnly): prove the dispatcher round-trip survives,
                    // but do not touch the repositioning pipeline.
                    if (ExperimentStage <= 2)
                    {
                        App.logger.Information($"[Stage2-Marshal] dispatcher round-trip OK (eventId=0x{eventId:X4}), pipeline untouched");
                        return;
                    }

                    // Passing all gates means: no in-flight keyboard sequence, no recent
                    // click, no recent key — i.e. a gesture-caused switch. Log the
                    // (negative) input evidence so every trigger decision is auditable.
                    App.logger.Debug($"ForegroundEvent: triggering mouse positioning (eventId=0x{eventId:X4}, lastKey={FormatAgeUtc(LastKeyboardActivityUtc)}, lastClick={FormatAgeUtc(LastMouseButtonDownUtc)})");

                    // Use the MANUAL trigger path (empty "old" rectangle) because by the time
                    // the Win32 event fires, the foreground window has ALREADY changed. Capturing
                    // the new foreground as "old" would make the comparison always equal and the
                    // cursor would never be moved. An empty old rect guarantees the comparison
                    // differs and the move is performed.
                    // The ForegroundHook trigger tag lets the shared pipeline tell this move
                    // apart from keyboard-initiated moves (see ShouldSkipMoveForForegroundHookPath).
                    TriggerManualMousePositioning(RepositionTrigger.ForegroundHook);
                    ExecuteMousePositioning();
                }),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        #endregion hook callback

        // Note: a log-only diagnostic WH_KEYBOARD_LL hook used to live here. It answered
        // its question (touchpad gestures do NOT inject synthetic keystrokes — zero
        // keyboard events during gesture tests, LLKHF_INJECTED always false) and was
        // removed: a per-keystroke Information-level logger should not ship.
        // See git history (commit 126685c) if it is ever needed again.

        #region experiment guard (single permitted touchpoint in the shared pipeline)

        /// <summary>
        /// Win11 multi-monitor bug workaround for the EXPERIMENT PATH ONLY.
        /// SetCursorPos to a window whose rect has negative left or top (foreground
        /// on a secondary monitor positioned left of/above the primary) returns
        /// success but ~9 seconds later ntdll raises an AccessViolationException we
        /// cannot catch. Skip the move for these targets.
        ///
        /// Gated by the reposition trigger, not by the global switch: only moves
        /// initiated by the foreground-hook path itself are guarded. Keyboard
        /// paths (Alt+Tab / Win+arrows / 2x Ctrl) always return false here and
        /// reach SetCursorPos exactly as on master — even when the experiment
        /// switch is ON.
        /// </summary>
        private bool ShouldSkipMoveForForegroundHookPath(Vanara.PInvoke.RECT windowRect)
        {
            if (ActiveTrigger != RepositionTrigger.ForegroundHook)
                return false;

            // Stage 3 (NoCursorMove): run the whole trigger/timer/geometry pipeline but
            // suppress the actual SetCursorPos. Isolates whether the crash needs the
            // cursor move itself or already happens in the pipeline above it.
            if (ExperimentStage == 3)
            {
                App.logger.Information(
                    $"[Stage3-NoCursorMove] would move cursor to center of rect (left={windowRect.left}, top={windowRect.top}, w={windowRect.Width}, h={windowRect.Height}), SetCursorPos suppressed");
                return true;
            }

            // Stage 4: legacy negative-coord guard, kept for A/B. It was added when
            // SetCursorPos to negative-coord windows was believed to trigger the delayed
            // crash; the root cause turned out to be the unrooted WinEventProc delegate
            // (see RegisterForegroundEventHooks), not SetCursorPos.
            // Stage 5: no coordinate guard at all — the real verdict on whether
            // negative-coord SetCursorPos is actually dangerous on this machine.
            if (ExperimentStage == 4)
            {
                if (windowRect.left >= 0 && windowRect.top >= 0)
                    return false;
                App.logger.Information(
                    $"TryMouseRepositioning: skipping move, foreground-hook path to negative-coord monitor (rect.left={windowRect.left}, rect.top={windowRect.top})");
                return true;
            }

            return false; // Stage 5: never skip
        }

        #endregion experiment guard
    }
}
