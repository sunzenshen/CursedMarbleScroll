using System;
using System.Windows.Forms;

namespace MarbleScroll
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            bool defaults = Array.IndexOf(args, "--defaults") >= 0;
            var settings = Properties.Settings.Default;
            Global.marbleScroll = new MarbleScroll
            {
                EnableMiddleButtonScroll = defaults || settings.EnableMiddleButtonScroll,
                EnableXButtonScroll = defaults || settings.EnableXButtonScroll,
                ConvertXToMiddleClick = !defaults && settings.ConvertXToMiddleClick,
                SmoothScrolling = (defaults || settings.SmoothScrolling) && Array.IndexOf(args, "--notches") < 0,
                LockEnabled = Array.IndexOf(args, "--lock") >= 0 || (!defaults && settings.LockEnabled),
                Reverse = !defaults && settings.Reverse
            };

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var form = new MarbleForm())
            {
                Global.marbleScroll.Start();
                try
                {
                    Application.Run();
                }
                finally
                {
                    Global.marbleScroll.Stop();
                }
            }
        }
    }
}
