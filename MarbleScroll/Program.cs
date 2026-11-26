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
                if (Array.IndexOf(args, "--no-lock") >= 0)
                    options.LockEnabled = false;
                if (Array.IndexOf(args, "--no-side-buttons") >= 0)
                    options.SideButtonsActAsMiddle = false;
                if (Array.IndexOf(args, "--snap-to-axis") >= 0)
                    options.SnapToAxis = true;
                if (Array.IndexOf(args, "--momentum") >= 0)
                    options.Momentum = true;
                if (Array.IndexOf(args, "--no-momentum") >= 0)
                    options.Momentum = false;
                options.LockWindowMs = ScrollSettings.BoundLockWindow(
                    ReadPositiveOption(args, "--lock-window-ms", options.LockWindowMs));
                if (Array.IndexOf(args, "--paused") >= 0)
                    options.Paused = true;

                var scroll = new MarbleScroll(options, ReadPositiveOption(args, "--lock-idle-ms", 10000));
                try
                {
                    scroll.Start();
                    try
                    {
                        RunTrayLoop(scroll, options, ReadPositiveOption(args, "--exit-after-ms", 0));
                    }
                    finally
                    {
                        scroll.Stop();
                    }
                    if (scroll.HookFailure != null)
                        throw scroll.HookFailure;
                    return 0;
                }
                catch (Exception error)
                {
                    Debug.WriteLine(error);
                    string message = error.Message;
                    Exception cause = error.GetBaseException();
                    if (cause != error)
                        message += Environment.NewLine + cause.Message;
                    MessageBox.Show(message, "MarbleScroll", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }
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
                    case SessionSwitchReason.RemoteConnect:
                        scroll.Reset(true);
                        break;
                    case SessionSwitchReason.SessionLock:
                    case SessionSwitchReason.ConsoleDisconnect:
                    case SessionSwitchReason.RemoteDisconnect:
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
                    // Create the UI marshaling handle before observing hook failures.
                    _ = form.Handle;
                    Action hookFailed = () =>
                    {
                        try
                        {
                            form.BeginInvoke((MethodInvoker)Application.ExitThread);
                        }
                        catch (InvalidOperationException)
                        {
                            // The tray is already closing. Main still observes the failure.
                        }
                    };
                    scroll.HookFailed += hookFailed;
                    try
                    {
                        if (scroll.HookFailure == null)
                            Application.Run();
                    }
                    finally
                    {
                        scroll.HookFailed -= hookFailed;
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
