using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MarbleScroll
{
    public partial class MarbleForm : Form
    {
        private readonly NotifyIcon tray;
        private bool saveWarningShown;

        internal MarbleForm(MarbleScroll scroll, ScrollOptions options, int exitAfterMs)
        {
            InitializeComponent();
            var resources = new ComponentResourceManager(typeof(MarbleForm));
            tray = new NotifyIcon(components)
            {
                ContextMenuStrip = contextMenuStrip1,
                Icon = (Icon)resources.GetObject("$this.Icon"),
                Text = "MarbleScroll",
                Visible = true
            };

            var settings = Properties.Settings.Default;
            Action<Action> change = save =>
            {
                scroll.UpdateOptions(options);
                SaveChoice(() =>
                {
                    save();
                    settings.Save();
                });
            };

            BindToggle(scrollOnMiddleButtonToolStripMenuItem, options.EnableMiddleButtonScroll,
                value => { options.EnableMiddleButtonScroll = value; change(() => settings.EnableMiddleButtonScroll = value); });
            BindToggle(scrollOnXButtonToolStripMenuItem, options.EnableXButtonScroll,
                value => { options.EnableXButtonScroll = value; change(() => settings.EnableXButtonScroll = value); });
            BindToggle(emulateMiddleButtonOnXClickToolStripMenuItem, options.ConvertXToMiddleClick,
                value => { options.ConvertXToMiddleClick = value; change(() => settings.ConvertXToMiddleClick = value); });
            contextMenuStrip1.Items.Remove(exitToolStripMenuItem);
            contextMenuStrip1.Items.Add(new ToolStripSeparator());
            AddToggle("Smooth scrolling", options.SmoothScrolling,
                value => { options.SmoothScrolling = value; change(() => settings.SmoothScrolling = value); });
            AddToggle("Double-click to lock scrolling", options.LockEnabled,
                value => { options.LockEnabled = value; change(() => settings.LockEnabled = value); });
            AddToggle("Reverse scroll direction", options.Reverse,
                value => { options.Reverse = value; change(() => settings.Reverse = value); });
            AddToggle("Snap to dominant axis", options.SnapToAxis,
                value => { options.SnapToAxis = value; change(() => settings.SnapToAxis = value); });
            AddSpeedMenu(options, change);

            contextMenuStrip1.Items.Add(new ToolStripSeparator());
            var pause = AddToggle("Pause", options.Paused, value =>
            {
                options.Paused = value;
                UpdateTooltip(value, scroll.State);
                change(() => settings.Paused = value);
            });
            tray.MouseDoubleClick += (sender, e) =>
            {
                if (e.Button == MouseButtons.Left)
                    pause.Checked = !pause.Checked;
            };
            contextMenuStrip1.Items.Add("Reset", null, (sender, e) => scroll.Reset(true));
            AddStartupToggle();
            contextMenuStrip1.Items.Add(new ToolStripSeparator());
            contextMenuStrip1.Items.Add(exitToolStripMenuItem);

            UpdateTooltip(options.Paused, scroll.State);
            var tooltipTimer = new Timer(components) { Interval = 100, Enabled = true };
            tooltipTimer.Tick += (sender, e) => UpdateTooltip(options.Paused, scroll.State);
            if (exitAfterMs > 0)
            {
                var exitTimer = new Timer(components) { Interval = exitAfterMs, Enabled = true };
                exitTimer.Tick += (sender, e) =>
                {
                    exitTimer.Stop();
                    exitToolStripMenuItem.PerformClick();
                };
            }
        }

        private void AddSpeedMenu(ScrollOptions options, Action<Action> change)
        {
            var menu = new ToolStripMenuItem("Scroll speed");
            AddSpeed(menu, "Slow (0.5x)", 0.5, options, change);
            AddSpeed(menu, "Normal (1x)", 1, options, change);
            AddSpeed(menu, "Fast (2x)", 2, options, change);
            AddSpeed(menu, "Very fast (3x)", 3, options, change);
            contextMenuStrip1.Items.Add(menu);
        }

        private static void AddSpeed(ToolStripMenuItem menu, string label, double speed,
            ScrollOptions options, Action<Action> change)
        {
            var item = new ToolStripMenuItem(label) { Checked = options.Speed == speed };
            item.Click += (sender, e) =>
            {
                foreach (ToolStripMenuItem choice in menu.DropDownItems)
                    choice.Checked = choice == item;
                options.Speed = speed;
                change(() => Properties.Settings.Default.ScrollSpeed = speed.ToString(CultureInfo.InvariantCulture));
            };
            menu.DropDownItems.Add(item);
        }

        private void AddStartupToggle()
        {
            var item = new ToolStripMenuItem("Start with Windows");
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(ScrollOptions.RunKey))
                    item.Checked = key?.GetValue(ScrollOptions.RunValue) is string;
            }
            catch (Exception error)
            {
                Debug.WriteLine(error);
            }
            item.Click += (sender, e) => SaveChoice(() =>
            {
                using (RegistryKey key = item.Checked
                    ? Registry.CurrentUser.OpenSubKey(ScrollOptions.RunKey, true)
                    : Registry.CurrentUser.CreateSubKey(ScrollOptions.RunKey))
                {
                    if (item.Checked)
                        key?.DeleteValue(ScrollOptions.RunValue, false);
                    else
                        key.SetValue(ScrollOptions.RunValue, "\"" + Application.ExecutablePath + "\"", RegistryValueKind.String);
                }
                item.Checked = !item.Checked;
            });
            contextMenuStrip1.Items.Add(item);
        }

        private void UpdateTooltip(bool paused, ScrollState state)
        {
            string text = paused ? "MarbleScroll (paused)" : state == ScrollState.LockedScroll
                ? "MarbleScroll (scroll locked)" : "MarbleScroll";
            if (tray.Text != text)
                tray.Text = text;
        }

        private void SaveChoice(Action save)
        {
            try
            {
                save();
            }
            catch (Exception error)
            {
                Debug.WriteLine(error);
                if (!saveWarningShown)
                {
                    saveWarningShown = true;
                    tray.ShowBalloonTip(4000, "MarbleScroll", "Could not save your settings.", ToolTipIcon.Warning);
                }
            }
        }

        private ToolStripMenuItem AddToggle(string label, bool initialValue, Action<bool> change)
        {
            var item = new ToolStripMenuItem(label);
            BindToggle(item, initialValue, change);
            contextMenuStrip1.Items.Add(item);
            return item;
        }

        private static void BindToggle(ToolStripMenuItem item, bool initialValue, Action<bool> change)
        {
            item.CheckOnClick = true;
            item.Checked = initialValue;
            item.CheckedChanged += (sender, e) => change(item.Checked);
        }

        private void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Application.ExitThread();
        }
    }
}
