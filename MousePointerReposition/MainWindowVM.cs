using FMUtils.KeyboardHook;
using Microsoft.Win32;
using MousePointerReposition.Helper;
using MousePointerReposition.ViewModel;
using Serilog;
using Serilog.Core;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;

namespace MousePointerReposition
{
    public class MainWindowVM : INotifyPropertyChanged
    {
        #region constants

        /// <summary>
        /// Timeout waiting for new foreground application 
        /// </summary>
        private const double CHECK_TIMEOUT = 5000; // ms

        /// <summary>
        /// Period for detecting multiple CTRL key pressing
        /// </summary>
        private const long CTRL_KEY_TIMEOUT = 1000; // ms

        #endregion constants


        #region fields

        private Vanara.PInvoke.RECT foreGroundWindowRectStart;
        private WindowState windowState;
        private bool showInTaskbar;
        private bool autostart;
        private RelayCommand loaded;
        private RelayCommand closing;
        private RelayCommand exit;
        private RelayCommand hide;
        private bool? disableAltTab;
        private bool? disableWinLeftRight;
        private bool? disableManualReposition;
        private bool? enableTouchpadAppSwitch;
        private bool autoStartDisabled;

        public event PropertyChangedEventHandler PropertyChanged;

        #endregion fields


        #region private properties

        private NotifyIcon NotifyIcon { get; set; }
        private Hook KeyboardHook { get; set; }
        private double CheckPeriod { get; set; }
        private bool IsMousePositioningTriggered { get; set; }
        private bool IsManualMousePositioningTriggered { get; set; }
        private int CtrlKeyPressCounter { get; set; }
        private DateTime CtrlKeyPressLast { get; set; }
        private System.Timers.Timer RepositioningTimer { get; set; }
        private System.Collections.Generic.List<Vanara.PInvoke.User32.HWINEVENTHOOK> ForegroundEventHooks { get; set; }
        private IntPtr LastForegroundHwnd { get; set; }

        #endregion private properties


        #region public properties

        /// <summary>
        /// Window loaded command
        /// </summary>
        public RelayCommand Loaded
        {
            get
            {
                if (loaded == null)
                    loaded = new RelayCommand(OnLoaded);
                return loaded;
            }
        }

        /// <summary>
        /// Window closing command
        /// </summary>
        public RelayCommand Closing
        {
            get
            {
                if (closing == null)
                    closing = new RelayCommand(OnClosing);
                return closing;
            }
        }

        /// <summary>
        /// Close programm
        /// </summary>
        public RelayCommand Exit
        {
            get
            {
                if (exit == null)
                    exit = new RelayCommand(OnExit);
                return exit;
            }
        }

        /// <summary>
        /// Hide window
        /// </summary>
        public RelayCommand Hide
        {
            get
            {
                if (hide == null)
                    hide = new RelayCommand(OnHide);
                return hide;
            }
        }

        

        /// <summary>
        /// Window state
        /// </summary>
        public WindowState WindowState
        {
            get => windowState;
            set
            {
                windowState = value;
                OnPropertyChanged(nameof(WindowState));
            }
        }

        /// <summary>
        /// Show in taskbar
        /// </summary>
        public bool ShowInTaskbar
        {
            get => showInTaskbar;
            set
            {
                showInTaskbar = value;
                OnPropertyChanged(nameof(ShowInTaskbar));
            }
        }





        public bool AutoStartDisabled
        {
            get => autoStartDisabled; set
            {
                autoStartDisabled = value;
                OnPropertyChanged(nameof(AutoStartDisabled));
            }
        }

        /// <summary>
        /// Autostart
        /// </summary>
        public bool Autostart
        {
            get
            {
                return autostart;
            }
            set
            {
                if (autostart != value) // value changed
                {
                    autostart = value;

#if _UWP
                    var startupTask = GetStartupTask();
                    if (value)
                    {
                        var task = startupTask.RequestEnableAsync().AsTask();
                        task.Wait();
                        SetAutostartState(task.Result);
                    }
                    else
                        startupTask.Disable();
#else
                    RegistryKey rkApp = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);
                    if (value)
                    {
                        string assemblyPath = Assembly.GetExecutingAssembly().Location;
                        rkApp.SetValue("MousePointerReposition", assemblyPath, RegistryValueKind.String);
                    }
                    else
                    {
                        rkApp.DeleteValue("MousePointerReposition");
                    }
                    
#endif
                    OnPropertyChanged(nameof(Autostart));
                }
            }
        }

        /// <summary>
        ///  Disable ALT+TAB key combination
        /// </summary>
        public bool DisableAltTab
        {
            get
            {
                if (!disableAltTab.HasValue)
                    disableAltTab = SettingsHelper.Instance.DisableAltTab;
                return disableAltTab ?? false;
            }
            set
            {
                disableAltTab = value;
                SettingsHelper.Instance.DisableAltTab = value;
                OnPropertyChanged(nameof(DisableAltTab));
            }
        }

        /// <summary>
        ///  Disable WIN+LEFT or RIGHT key combination
        /// </summary>
        public bool DisableWinLeftRight
        {
            get
            {
                if (!disableWinLeftRight.HasValue)
                    disableWinLeftRight = SettingsHelper.Instance.DisableWinLeftRight;
                return disableWinLeftRight ?? false;
            }
            set
            {
                disableWinLeftRight = value;
                SettingsHelper.Instance.DisableWinLeftRight = value;
                OnPropertyChanged(nameof(DisableWinLeftRight));
            }

        }

        /// <summary>
        /// Disable manual cursor repositioning (2x CTRL key)
        /// </summary>
        public bool DisableManuelPositioning
        {
            get
            {
                if (!disableManualReposition.HasValue)
                    disableManualReposition = SettingsHelper.Instance.DisableManualCursorReposition;
                return disableManualReposition ?? false;
            }
            set
            {
                disableManualReposition = value;
                SettingsHelper.Instance.DisableManualCursorReposition = value;
                OnPropertyChanged(nameof(DisableManuelPositioning));
            }
        }

        /// <summary>
        /// Enable mouse cursor repositioning when the foreground window changes
        /// via touchpad gestures or other system-level foreground transitions
        /// (Task View, snap assist, etc.). Default: ON.
        /// </summary>
        public bool EnableTouchpadAppSwitch
        {
            get
            {
                if (!enableTouchpadAppSwitch.HasValue)
                    enableTouchpadAppSwitch = SettingsHelper.Instance.EnableTouchpadAppSwitch;
                return enableTouchpadAppSwitch ?? true; // default ON
            }
            set
            {
                enableTouchpadAppSwitch = value;
                SettingsHelper.Instance.EnableTouchpadAppSwitch = value;
                OnPropertyChanged(nameof(EnableTouchpadAppSwitch));
            }
        }

        #endregion public properties


        #region .ctor

        /// <summary>
        /// .ctor
        /// </summary>
        public MainWindowVM()
        {
            App.logger.Debug("MainWindowVM entry");

            WindowState = WindowState.Minimized;
            ShowInTaskbar = true;

            // timer 
            RepositioningTimer = new System.Timers.Timer(200);
            RepositioningTimer.Elapsed += RepositioningTimer_Elapsed;
            RepositioningTimer.AutoReset = false;

            // setup keyboard hook
            KeyboardHook = new Hook("Global Keyboard Hook");
            KeyboardHook.KeyUpEvent += KeyUpHook;
            KeyboardHook.KeyDownEvent += KeyDownHook;

            // check autostart task state
            AutoStartDisabled = true;
            RefreshAutostartState();

            App.logger.Debug("MainWindowVM exit");
        }

        #endregion .ctor


        #region private methods
        /// <summary>
        /// Window on load
        /// </summary>
        /// <param name="state"></param>
        private void OnLoaded(object state)
        {
            App.logger.Debug("OnLoaded entry");
            // notifyIcon
            App.logger.Debug("NotifyIcon init");
            NotifyIcon = new NotifyIcon();

            System.IO.Stream iconStream = this.GetType().Assembly.GetManifestResourceStream("MousePointerReposition.Icons.mouse_gray.ico");

            NotifyIcon.Icon = new System.Drawing.Icon(iconStream);
            NotifyIcon.Visible = true;
            NotifyIcon.Click += NotifyIcon_Click;
            App.logger.Debug("NotifyIcon done");


            // use background dispatcher
            System.Windows.Application.Current.Dispatcher.BeginInvoke(
                new Action(() =>
                    {
                        ShowInTaskbar = false;
                    }),
                System.Windows.Threading.DispatcherPriority.Background);

            // register foreground event hooks
            //   - EVENT_SYSTEM_FOREGROUND       : standard foreground change (Alt+Tab, snap, etc.)
            //   - EVENT_SYSTEM_SWITCHSTART..END : switcher UI lifecycle (covers Win11 three-finger
            //                                     gesture which may not always fire FOREGROUND)
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

            App.logger.Debug("OnLoaded exit");
        }

        /// <summary>
        /// Window in closing
        /// </summary>
        /// <param name="state"></param>
        private void OnClosing(object state)
        {
            App.logger.Debug("OnClosing");

            // unhook foreground event
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

            NotifyIcon.Visible = false;
        }

        /// <summary>
        /// Exit programm
        /// </summary>
        /// <param name="state"></param>
        private void OnExit(object state)
        {
            App.logger.Debug("OnExit");
            System.Windows.Application.Current.MainWindow.Close();
        }

        /// <summary>
        /// Hide window
        /// </summary>
        /// <param name="state"></param>
        private void OnHide(object state)
        {
            App.logger.Debug("OnHide");
            WindowState = WindowState.Minimized;
            ShowInTaskbar = false;
        }

        /// <summary>
        /// Retries to move the mouse cursor to the center of a new active application window for a specific amount of time (CHECK_TIMEOUT).
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void RepositioningTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            CheckPeriod += RepositioningTimer.Interval;

            // check timeout
            // and only restart if repositiong does not occur
            if (CheckPeriod < CHECK_TIMEOUT
                && !TryMouseRepositioning())
            {
                RepositioningTimer.Start();
            }
        }

        /// <summary>
        /// Open main window when notification icon is clicked
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void NotifyIcon_Click(object sender, EventArgs e)
        {
            App.logger.Debug("NotifyIcon_Click");
            if (WindowState != WindowState.Minimized)
            {
                WindowState = WindowState.Minimized;
                ShowInTaskbar = false;
            }
            else
            {
                WindowState = WindowState.Normal;
                ShowInTaskbar = true;
                System.Windows.Application.Current.MainWindow.Focus();
            }
        }

        /// <summary>
        /// Win32 event hook callback (fires on the hook thread, not the UI thread).
        /// Filters out self-window and dedupes rapid foreground churn, then
        /// marshals the trigger into the WPF UI thread.
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
                    TriggerManualMousePositioning();
                    ExecuteMousePositioning();
                }),
                System.Windows.Threading.DispatcherPriority.Background);
        }


        /// <summary>
        /// Keyboard key down event handler method
        /// </summary>
        /// <param name="e"></param>
        private void KeyDownHook(KeyboardHookEventArgs e)
        {
            App.logger.Debug("KeyDownHook entry");
            // win + shift + left or right key
            if (!DisableWinLeftRight)
            {
                if (e.Key == Keys.Left
                    || e.Key == Keys.Right && e.isWinPressed) // with or without shift //&& e.isShiftPressed)
                {
                    App.logger.Debug("Win+Left or Win+Right");
                    TriggerMousePositioning();
                }
            }

            // alt + tap
            if (!DisableAltTab)
            {
                if (e.Key == Keys.Tab && e.isAltPressed)
                {
                    App.logger.Debug("Alt+Tab");
                    TriggerMousePositioning();
                }
            }

            if (!DisableManuelPositioning)
            {
                if (e.isCtrlPressed && e.Key == Keys.None)
                {
                    App.logger.Debug("Ctrl pressed");
                    CtrlKeyPressCounter++;

                    if (CtrlKeyPressCounter == 1)
                    {

                        CtrlKeyPressLast = DateTime.Now;
                    }


                    if (CtrlKeyPressCounter >= 2)
                    {
                        App.logger.Debug("2xCtrl");
                        if ((DateTime.Now - CtrlKeyPressLast).TotalMilliseconds <= CTRL_KEY_TIMEOUT)
                        {
                            TriggerManualMousePositioning();
                        }
                        else
                        {
                            CtrlKeyPressLast = DateTime.Now;
                            CtrlKeyPressCounter = 1;
                        }
                    }
                }

                if (e.isCtrlPressed && e.Key != Keys.None)
                {
                    CtrlKeyPressCounter = 0;
                    IsManualMousePositioningTriggered = false;
                }
            }

            App.logger.Debug("KeyDownHook exit");
        }

        /// <summary>
        /// Keyboard key up event handler method
        /// </summary>
        /// <param name="e"></param>
        private void KeyUpHook(KeyboardHookEventArgs e)
        {
            App.logger.Debug("KeyUpHook entry");
            // when all keys are released
            if (e.Key == Keys.None && IsMousePositioningTriggered)
            {
                ExecuteMousePositioning();
            }

            if (!DisableManuelPositioning)
            {
                if (e.isCtrlPressed && e.Key == Keys.None && IsManualMousePositioningTriggered)
                {
                    CtrlKeyPressCounter = 0;
                    IsManualMousePositioningTriggered = false;
                    ExecuteMousePositioning();
                }
            }
            App.logger.Debug("KeyUpHook exit");
        }


        /// <summary>
        /// Called when key combination for cursor repositioning is detected.
        /// Stores current foreground window geometry.
        /// </summary>
        private void TriggerMousePositioning()
        {
            App.logger.Debug("TriggerMousePositioning enter");
            RepositioningTimer.Stop(); // stop currently executing repositioning
            IsMousePositioningTriggered = true;

            // save current foreground window rectangle
            var foregroundWindowHandleStart = Vanara.PInvoke.User32.GetForegroundWindow();
            foreGroundWindowRectStart = new Vanara.PInvoke.RECT();
            Vanara.PInvoke.User32.GetWindowRect(foregroundWindowHandleStart, out foreGroundWindowRectStart);
            App.logger.Debug("TriggerMousePositioning exit");
        }


        /// <summary>
        /// Called when key combination for manual cursor repositioning is detected.
        /// Stores current foreground window geometry.
        /// </summary>
        private void TriggerManualMousePositioning()
        {
            App.logger.Debug("TriggerManualMousePositioning start");
            RepositioningTimer.Stop(); // stop currently executing repositioning
            IsManualMousePositioningTriggered = true;

            // save empty current foreground window rectangle
            foreGroundWindowRectStart = new Vanara.PInvoke.RECT();
            App.logger.Debug("TriggerManualMousePositioning exit");
        }


        /// <summary>
        /// Start timer for trying cursor repositining to a new foreground application window.
        /// </summary>
        private void ExecuteMousePositioning()
        {
            App.logger.Debug("ExecuteMousePositioning start");
            IsMousePositioningTriggered = false;

            CheckPeriod = 0;
            // start timer that checks active window change (rectangle change)
            RepositioningTimer.Start();
            App.logger.Debug("ExecuteMousePositioning end");
        }


        /// <summary>
        /// Try to move cursor to the center of a new foreground application window.
        /// The active foreground window is compared with the active window before pressing the triggering key combinations.
        /// If the active foreground window has changed the mouse cursor is only repositioned 
        /// if the current cursor location is not already within the foreground window rectangle.
        /// </summary>
        /// <returns></returns>
        private bool TryMouseRepositioning()
        {
            App.logger.Debug("TryMouseRepositioning start");
            // get foreground window
            var foregroundWindowHandle = Vanara.PInvoke.User32.GetForegroundWindow();
            Vanara.PInvoke.User32.GetWindowRect(foregroundWindowHandle, out Vanara.PInvoke.RECT windowRect);

            App.logger.Debug(String.Format("Old window: left={0} top={1} width={2} height={3}", foreGroundWindowRectStart.left, foreGroundWindowRectStart.top, foreGroundWindowRectStart.Width, foreGroundWindowRectStart.Height));
            App.logger.Debug(String.Format("New window: left={0} top={1} width={2} height={3}", windowRect.left, windowRect.top, windowRect.Width, windowRect.Height));


            if (foreGroundWindowRectStart.left != windowRect.left
                || foreGroundWindowRectStart.Width != windowRect.Width
                || foreGroundWindowRectStart.top != windowRect.top
                || foreGroundWindowRectStart.Height != windowRect.Height)
            {
                // check if mouse position is within new active application window
                // get current cursor position
                Vanara.PInvoke.User32.GetCursorPos(out System.Drawing.Point cursorPos);

                App.logger.Debug(String.Format("Current cursor: x={0} y={1}", cursorPos.X, cursorPos.Y));

                if (windowRect.left <= cursorPos.X
                    && windowRect.right >= cursorPos.X
                    && windowRect.top <= cursorPos.Y
                    && windowRect.bottom >= cursorPos.Y)
                {
                    return true;
                }
                else
                {
                    int x = windowRect.left + windowRect.Width / 2;
                    int y = windowRect.top + windowRect.Height / 2;

                    Debug.WriteLine(String.Format("New cursor: x={0} y={1}", x, y));

                    // Win11 multi-monitor bug workaround: SetCursorPos to a window whose
                    // rect has negative left or top (i.e. foreground is on a secondary
                    // monitor positioned to the left of the primary) returns success
                    // but ~9 seconds later ntdll raises an AccessViolationException that
                    // we cannot catch from user mode. Skip the move entirely for these
                    // targets so the platform bug stays untriggered. Same logic as the
                    // existing Alt+Tab path in this binary; the keyboard path also uses
                    // TryMouseRepositioning + SetCursorPos, it just usually doesn't
                    // land on a negative-coord target because users tend to keep
                    // Alt+Tab on the primary monitor.
                    if (windowRect.left < 0 || windowRect.top < 0)
                    {
                        App.logger.Information(
                            $"TryMouseRepositioning: skipping move, foreground window on monitor with negative coords (rect.left={windowRect.left}, rect.top={windowRect.top})");
                        return true;
                    }

                    Vanara.PInvoke.User32.SetCursorPos(x + 1, y + 1); // sometimes cursor is not positioned right
                    Vanara.PInvoke.User32.SetCursorPos(x, y); // calling twice sets the correct postion

                    App.logger.Debug("TryMouseRepositioning end");
                    return true;
                }
            }
            else
            {
                App.logger.Debug("TryMouseRepositioning end");
                return false;
            }
                

            
        }


#if _UWP
        /// <summary>
        /// Get windows universal app startup task.
        /// </summary>
        /// <returns></returns>
        private Windows.ApplicationModel.StartupTask GetStartupTask()
        {
            var task = Windows.ApplicationModel.StartupTask.GetAsync("MousePointerRepositionStartupTask").AsTask();
            task.Wait();
            return task.Result;
        }

        /// <summary>
        /// Get windows universal app startup task.
        /// </summary>
        private void GetStartupTaskAndContinue(Action<Task<Windows.ApplicationModel.StartupTask>> action)
        {
            var task = Windows.ApplicationModel.StartupTask.GetAsync("MousePointerRepositionStartupTask").AsTask();
            task.ContinueWith(action);
        }
    
        /// <summary>
        /// Get start task state and set AutostartDisabled and Autostart property values;
        /// </summary>
        private void RefreshAutostartState()
        {
            GetStartupTaskAndContinue((t) =>
            {
                SetAutostartState(t.Result.State);
            });
        }

        /// <summary>
        /// Set AutostartDisabled and Autostart property view startup task state (universal app startup)
        /// </summary>
        /// <param name="startupTask"></param>
        private void SetAutostartState(Windows.ApplicationModel.StartupTaskState startupTaskState)
        {
            AutoStartDisabled = startupTaskState == Windows.ApplicationModel.StartupTaskState.DisabledByUser ||
                    startupTaskState == Windows.ApplicationModel.StartupTaskState.DisabledByPolicy ||
                    startupTaskState == Windows.ApplicationModel.StartupTaskState.EnabledByPolicy;

            autostart = startupTaskState == Windows.ApplicationModel.StartupTaskState.Enabled ||
                startupTaskState == Windows.ApplicationModel.StartupTaskState.EnabledByPolicy;
            OnPropertyChanged(nameof(Autostart));
        }
#else
        /// <summary>
        /// Get start task state and set AutostartDisabled and Autostart property values;
        /// </summary>
        private void RefreshAutostartState()
        {
            AutoStartDisabled = false;

            RegistryKey rkApp = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);
            string appPath = rkApp.GetValue("MousePointerReposition")?.ToString();
            string assemblyPath = Assembly.GetExecutingAssembly().Location;
            if (appPath == null || !assemblyPath.Equals(appPath))
            {
                autostart = false;
            }
            else
            {
                autostart = true;
            }
            OnPropertyChanged(nameof(Autostart));
        }
#endif

        #endregion private methods


        #region INotifyPropertyChanged
        /// <summary>
        /// Raises this object's PropertyChanged event.
        /// </summary>
        /// <param name="propertyName">The property that has a new value.</param>
        protected virtual void OnPropertyChanged(string propertyName)
        {
            if (PropertyChanged != null)
            {
                PropertyChangedEventArgs e = new PropertyChangedEventArgs(propertyName);
                PropertyChanged(this, e);
            }
        }
        #endregion INotifyPropertyChanged
    }

}
