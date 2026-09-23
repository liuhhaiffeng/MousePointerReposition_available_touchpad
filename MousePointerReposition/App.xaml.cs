using Serilog;
using Serilog.Core;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.IO;
using System.Linq;
using System.Windows;

namespace MousePointerReposition
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        public static LoggingLevelSwitch loggingLevelSwitch;
        public static Logger logger;

        public App()
        {
            string appFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MousePointerReposition");
            string appLogFolder = Path.Combine(appFolder, "Logs");

            if(!Directory.Exists(appLogFolder))
                Directory.CreateDirectory(appLogFolder);


            loggingLevelSwitch = new LoggingLevelSwitch();
            loggingLevelSwitch.MinimumLevel = Serilog.Events.LogEventLevel.Information;

            if (File.Exists(Path.Combine(appFolder, "debug.txt")))
                loggingLevelSwitch.MinimumLevel = Serilog.Events.LogEventLevel.Debug;

            logger = new LoggerConfiguration()
                .MinimumLevel.ControlledBy(loggingLevelSwitch)
                .WriteTo.File(Path.Combine(appLogFolder, "MousePointerReposition.log.txt"), rollingInterval: RollingInterval.Day)
                  .CreateLogger();
        }

        private void Application_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            logger.Error("Application_DispatcherUnhandledException", e.Exception);
            // Mark handled so the dispatcher keeps running instead of tearing the process down.
            // Without this, any WPF-internal exception during message processing (which we
            // cannot catch from our own callbacks — see VS stack trace: GetMessageW ->
            // PushFrameImpl -> Application.Run) terminates the whole process with
            // STATUS_FATAL_USER_CALLBACK_EXCEPTION (0xC000041D).
            e.Handled = true;
        }
    }
}
