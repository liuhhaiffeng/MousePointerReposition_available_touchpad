using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace MousePointerReposition.Helper
{
    public class SettingsHelper
    {
        #region instance
        private static SettingsHelper _instance;

        /// <summary>
        /// Get single instance
        /// </summary>
        public static SettingsHelper Instance
        {
            get
            {
                if (_instance == null)
                    _instance = new SettingsHelper();
                return _instance;
            }
        }
        #endregion instance


        #region public properties
        
        /// <summary>
        /// Disable ALT+TAB key combination
        /// </summary>
        public bool DisableAltTab
        {
            get { return ReadRegValue<int>(nameof(DisableAltTab)) == 1; }
            set { WriteRegValue<int>(nameof(DisableAltTab), value ? 1 : 0); }
        }

        /// <summary>
        /// Disable WIN+LEFT or RIGHT key combination
        /// </summary>
        public bool DisableWinLeftRight
        {
            get { return ReadRegValue<int>(nameof(DisableWinLeftRight)) == 1; }
            set { WriteRegValue<int>(nameof(DisableWinLeftRight), value ? 1 : 0); }
        }

        /// <summary>
        /// Disable manual cursor reposition (2x CTRL key)
        /// </summary>
        public bool DisableManualCursorReposition
        {
            get { return ReadRegValue<int>(nameof(DisableManualCursorReposition)) == 1; }
            set { WriteRegValue<int>(nameof(DisableManualCursorReposition), value ? 1 : 0); }
        }

        /// <summary>
        /// Enable touchpad / system-foreground mouse cursor repositioning.
        /// Repositions the mouse cursor when the foreground window changes via
        /// touchpad gestures or other system-level foreground transitions
        /// (Task View, snap assist, etc.).
        /// </summary>
        public bool EnableTouchpadAppSwitch
        {
            get { return ReadRegValue<int>(nameof(EnableTouchpadAppSwitch)) != 0; }
            set { WriteRegValue<int>(nameof(EnableTouchpadAppSwitch), value ? 1 : 0); }
        }

        /// <summary>
        /// Touchpad experiment stage — internal debugging switch (registry only, no UI).
        /// Only consulted when EnableTouchpadAppSwitch is ON. Bisects the crashing path:
        ///   0/unset = treated as 1 (ObserveOnly)
        ///   1 = ObserveOnly  — foreground callback only logs event details, NO dispatcher marshal
        ///   2 = MarshalOnly  — + Dispatcher.BeginInvoke round-trip, lambda logs and returns
        ///   3 = NoCursorMove — + full trigger/timer/geometry pipeline, SetCursorPos suppressed
        ///   4 = Full         — complete behavior with legacy negative-coord guard
        ///   5 = FullNoGuard  — complete behavior WITHOUT the negative-coordinate guard
        ///                      (the guard's premise — SetCursorPos causing the crash —
        ///                      was disproven; root cause was the unrooted WinEventProc
        ///                      delegate. Stage 5 is the real verdict on this machine)
        /// Captured at hook registration; restart the app after changing.
        /// </summary>
        public int TouchpadExperimentStage
        {
            get { return ReadRegValue<int>(nameof(TouchpadExperimentStage)); }
            set { WriteRegValue<int>(nameof(TouchpadExperimentStage), value); }
        }
        #endregion public properties


        #region helpers
        /// <summary>
        /// Read value from registry
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="key"></param>
        /// <returns></returns>
        private T ReadRegValue<T>(string key)
        {
            try
            {
                return (T)Registry.CurrentUser.CreateSubKey("Software\\rammelhof.at\\MousePointerReposition").GetValue(key, false);
            }
            catch
            {
                // and set default
                WriteRegValue<T>(key, default(T));
                return default(T);
            }
        }

        /// <summary>
        /// Read value from registry
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="key"></param>
        /// <returns></returns>
        private void WriteRegValue<T>(string key, T value)
        {
            Registry.CurrentUser.CreateSubKey("Software\\rammelhof.at\\MousePointerReposition").SetValue(key, value);
        }
        #endregion helpers


        #region autostart
        /// <summary>
        /// Create or remove autostart entry
        /// </summary>
        /// <param name="autostart"></param>
        private void SetAutostart(bool autostart)
        {
            if (autostart)
            {
                string path = Assembly.GetExecutingAssembly().Location;
                Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run").SetValue("MousePointerReposition", String.Format("\"{0}\"", path));
            }
            else
                Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run").DeleteValue("MousePointerReposition");
        }

        /// <summary>
        /// Exists autostart entry
        /// </summary>
        /// <returns></returns>
        private bool GetAutostart()
        {
            var value = Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run").GetValue("MousePointerReposition", null);
            return value != null;
        }
        #endregion autostart
    }
}
