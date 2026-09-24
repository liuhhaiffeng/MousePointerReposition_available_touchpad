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

        // Diagnostic low-level keyboard hook (log-only). Reads KBDLLHOOKSTRUCT.flags
        // so we can tell physical keystrokes from synthetic/injected ones (e.g. if the
        // touchpad gesture stack injects Alt+Tab). Never interferes: it only logs and
        // immediately calls CallNextHookEx. Registered only while the experiment is ON.
        private Vanara.PInvoke.User32.SafeHHOOK DiagnosticKeyboardHook;
        private Vanara.PInvoke.User32.HookProc DiagnosticKeyboardProcDelegate; // field: keeps the delegate alive against GC

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
            if (stage < 1 || stage > 5) stage = 1; // 0/unset → ObserveOnly (safest)
            ExperimentStage = stage;
            App.logger.Information($"Foreground event hook init: EnableTouchpadAppSwitch={EnableTouchpadAppSwitch}, ExperimentStage={ExperimentStage}");
            ForegroundEventHooks = new System.Collections.Generic.List<Vanara.PInvoke.User32.HWINEVENTHOOK>();
            if (EnableTouchpadAppSwitch)
            {
                // root the callback delegate BEFORE registering (see field comment)
                ForegroundEventProcDelegate = ForegroundEventCallback;

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

                var hookSwitch = Vanara.PInvoke.User32.SetWinEventHook(
                    Vanara.PInvoke.User32.EventConstants.EVENT_SYSTEM_SWITCHSTART,
                    Vanara.PInvoke.User32.EventConstants.EVENT_SYSTEM_SWITCHEND,
                    Vanara.PInvoke.HINSTANCE.NULL,
                    ForegroundEventProcDelegate,
                    0,
                    0,
                    Vanara.PInvoke.User32.WINEVENT.WINEVENT_OUTOFCONTEXT);
                if (hookSwitch.IsNull)
                    App.logger.Warning("Failed to register EVENT_SYSTEM_SWITCHSTART..END hook");
                else
                    ForegroundEventHooks.Add(hookSwitch);

                App.logger.Debug($"Foreground event hooks registered: {ForegroundEventHooks.Count}/2");

                // diagnostic keyboard hook (log-only, observes the INJECTED flag)
                RegisterDiagnosticKeyboardHook();
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

            if (DiagnosticKeyboardHook != null && !DiagnosticKeyboardHook.IsNull)
            {
                App.logger.Debug("UnhookWindowsHookEx (diagnostic keyboard hook)");
                Vanara.PInvoke.User32.UnhookWindowsHookEx(DiagnosticKeyboardHook); // SafeHHOOK → HHOOK implicit
                DiagnosticKeyboardHook = null;
            }
        }

        #endregion hook lifecycle

        #region hook callback

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

            // SWITCHSTART just signals that a switcher UI appeared — do not trigger repositioning yet.
            // FOREGROUND and SWITCHEND both indicate the user has finished the gesture and the new
            // target app may now be foreground (or about to be).
            if (eventId == Vanara.PInvoke.User32.EventConstants.EVENT_SYSTEM_SWITCHSTART)
            {
                return;
            }

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
                    // Stage 2 (MarshalOnly): prove the dispatcher round-trip survives,
                    // but do not touch the repositioning pipeline.
                    if (ExperimentStage <= 2)
                    {
                        App.logger.Information($"[Stage2-Marshal] dispatcher round-trip OK (eventId=0x{eventId:X4}), pipeline untouched");
                        return;
                    }

                    App.logger.Debug($"ForegroundEvent: triggering mouse positioning (eventId=0x{eventId:X4})");

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

        #region diagnostic keyboard hook (log-only instrumentation)

        /// <summary>
        /// Registers a log-only WH_KEYBOARD_LL hook that reports the LLKHF_INJECTED
        /// flag for every key event. This distinguishes physical keystrokes from
        /// synthetic ones injected by the shell (e.g. if the precision-touchpad
        /// gesture stack implements 'switch apps' by injecting Alt+Tab).
        /// Only registered while the experiment switch is ON.
        /// </summary>
        private void RegisterDiagnosticKeyboardHook()
        {
            DiagnosticKeyboardProcDelegate = DiagnosticKeyboardProc;
            DiagnosticKeyboardHook = Vanara.PInvoke.User32.SetWindowsHookEx(
                Vanara.PInvoke.User32.HookType.WH_KEYBOARD_LL,
                DiagnosticKeyboardProcDelegate,
                Vanara.PInvoke.HINSTANCE.NULL,
                0);
            if (DiagnosticKeyboardHook.IsNull)
                App.logger.Warning("[DiagKey] diagnostic keyboard hook failed to register");
            else
                App.logger.Information("[DiagKey] diagnostic keyboard hook registered (log-only)");
        }

        private IntPtr DiagnosticKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var kb = System.Runtime.InteropServices.Marshal.PtrToStructure<Vanara.PInvoke.User32.KBDLLHOOKSTRUCT>(lParam);
                var flags = kb.flags; // uint: bit0 = LLKHF_INJECTED, bit1 = LLKHF_LOWER_IL_INJECTED
                bool injected = (flags & 0x1) != 0;          // LLKHF_INJECTED
                bool lowerIlInjected = (flags & 0x2) != 0;   // LLKHF_LOWER_IL_INJECTED
                App.logger.Information($"[DiagKey] vk=0x{kb.vkCode:X2} scan=0x{kb.scanCode:X2} msg=0x{wParam.ToInt64():X4} injected={injected} lowerIL={lowerIlInjected}");
            }
            // hhk of CallNextHookEx is ignored by the system; pass a null handle.
            return Vanara.PInvoke.User32.CallNextHookEx(new Vanara.PInvoke.User32.HHOOK(IntPtr.Zero), nCode, wParam, lParam);
        }

        #endregion diagnostic keyboard hook (log-only instrumentation)

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
