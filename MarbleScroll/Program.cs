using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MarbleScroll
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            int checkBits = ReadPositiveOption(args, "--check-native-layouts", 0);
            if (checkBits != 0)
                return MarbleScroll.CheckNativeLayouts(checkBits) ? 0 : 1;

            Mutex instance;
            bool first;
            try
            {
                instance = new Mutex(false, @"Local\MarbleScroll.SingleInstance", out first);
            }
            catch (UnauthorizedAccessException error)
            {
                // An existing instance may run at a different integrity level.
                Debug.WriteLine(error);
                return 0;
            }
            using (instance)
            {
                if (!first)
                    return 0;

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                ScrollOptions options = ScrollOptions.Load(Array.IndexOf(args, "--defaults") >= 0);
                if (Array.IndexOf(args, "--notches") >= 0)
                    options.SmoothScrolling = false;
                if (Array.IndexOf(args, "--lock") >= 0)
                    options.LockEnabled = true;
                if (Array.IndexOf(args, "--paused") >= 0)
                    options.Paused = true;

                var scroll = new MarbleScroll(options, ReadPositiveOption(args, "--lock-idle-ms", 10000));
                RunTrayLoop(scroll, options, ReadPositiveOption(args, "--exit-after-ms", 0));
                return 0;
            }
        }

        private static void RunTrayLoop(MarbleScroll scroll, ScrollOptions options, int exitAfterMs)
        {
            SessionSwitchEventHandler session = (sender, args) =>
            {
                switch (args.Reason)
                {
                    case SessionSwitchReason.SessionUnlock:
                    case SessionSwitchReason.ConsoleConnect:
                        scroll.Reset(true);
                        break;
                    case SessionSwitchReason.SessionLock:
                    case SessionSwitchReason.ConsoleDisconnect:
                        scroll.Reset(false);
                        break;
                }
            };
            PowerModeChangedEventHandler power = (sender, args) =>
            {
                if (args.Mode == PowerModes.Suspend || args.Mode == PowerModes.Resume)
                    scroll.Reset(args.Mode == PowerModes.Resume);
            };
            EventHandler display = (sender, args) => scroll.Reset(false);

            try
            {
                // Subscribe before the tray installs a WinForms synchronization context.
                SystemEvents.SessionSwitch += session;
                SystemEvents.PowerModeChanged += power;
                SystemEvents.DisplaySettingsChanged += display;
                using (var form = new MarbleForm(scroll, options, exitAfterMs))
                {
                    try
                    {
                        scroll.Start();
                        Application.Run();
                    }
                    finally
                    {
                        scroll.Stop();
                    }
                }
            }
            finally
            {
                SystemEvents.SessionSwitch -= session;
                SystemEvents.PowerModeChanged -= power;
                SystemEvents.DisplaySettingsChanged -= display;
            }
        }

        private static int ReadPositiveOption(string[] args, string name, int fallback)
        {
            int position = Array.IndexOf(args, name);
            return position >= 0 && position + 1 < args.Length &&
                int.TryParse(args[position + 1], out int value) && value > 0 ? value : fallback;
        }
    }
}
