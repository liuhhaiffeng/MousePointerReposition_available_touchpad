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
            App.logger.Debug("Foreground event hook init");
            ForegroundEventHooks = new System.Collections.Generic.List<Vanara.PInvoke.User32.HWINEVENTHOOK>();
            if (EnableTouchpadAppSwitch)
            {
                var hookFront = Vanara.PInvoke.User32.SetWinEventHook(
                    Vanara.PInvoke.User32.EventConstants.EVENT_SYSTEM_FOREGROUND,
                    Vanara.PInvoke.User32.EventConstants.EVENT_SYSTEM_FOREGROUND,
                    Vanara.PInvoke.HINSTANCE.NULL,
                    ForegroundEventCallback,
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
                    ForegroundEventCallback,
                    0,
                    0,
                    Vanara.PInvoke.User32.WINEVENT.WINEVENT_OUTOFCONTEXT);
                if (hookSwitch.IsNull)
                    App.logger.Warning("Failed to register EVENT_SYSTEM_SWITCHSTART..END hook");
                else
                    ForegroundEventHooks.Add(hookSwitch);

                App.logger.Debug($"Foreground event hooks registered: {ForegroundEventHooks.Count}/2");
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
            if (windowRect.left >= 0 && windowRect.top >= 0)
                return false;
            App.logger.Information(
                $"TryMouseRepositioning: skipping move, foreground-hook path to negative-coord monitor (rect.left={windowRect.left}, rect.top={windowRect.top})");
            return true;
        }

        #endregion experiment guard
    }
}
