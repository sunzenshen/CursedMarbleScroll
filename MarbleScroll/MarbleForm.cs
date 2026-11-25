using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace MarbleScroll
{
    public partial class MarbleForm : Form
    {
        public MarbleForm()
        {
            InitializeComponent();

            MarbleScroll scroll = Global.marbleScroll;
            BindToggle(scrollOnMiddleButtonToolStripMenuItem, scroll.EnableMiddleButtonScroll,
                value => Properties.Settings.Default.EnableMiddleButtonScroll = scroll.EnableMiddleButtonScroll = value);
            BindToggle(scrollOnXButtonToolStripMenuItem, scroll.EnableXButtonScroll,
                value => Properties.Settings.Default.EnableXButtonScroll = scroll.EnableXButtonScroll = value);
            BindToggle(emulateMiddleButtonOnXClickToolStripMenuItem, scroll.ConvertXToMiddleClick,
                value => Properties.Settings.Default.ConvertXToMiddleClick = scroll.ConvertXToMiddleClick = value);
            AddToggle("Smooth scrolling", scroll.SmoothScrolling,
                value => Properties.Settings.Default.SmoothScrolling = scroll.SmoothScrolling = value);
            AddToggle("Double-click to lock scrolling", scroll.LockEnabled,
                value => Properties.Settings.Default.LockEnabled = scroll.LockEnabled = value);
            AddToggle("Reverse scroll direction", scroll.Reverse,
                value => Properties.Settings.Default.Reverse = scroll.Reverse = value);

            var resources = new ComponentResourceManager(typeof(MarbleForm));
            new NotifyIcon(components)
            {
                ContextMenuStrip = contextMenuStrip1,
                Icon = (Icon)resources.GetObject("$this.Icon"),
                Text = "MarbleScroll",
                Visible = true
            };
        }

        private void AddToggle(string label, bool initialValue, Action<bool> change)
        {
            var item = new ToolStripMenuItem(label);
            BindToggle(item, initialValue, change);
            contextMenuStrip1.Items.Add(item);
        }

        private static void BindToggle(ToolStripMenuItem item, bool initialValue, Action<bool> change)
        {
            item.CheckOnClick = true;
            item.Checked = initialValue;
            item.CheckedChanged += (sender, e) =>
            {
                change(item.Checked);
                Properties.Settings.Default.Save();
            };
        }

        private void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Application.Exit();
        }
    }
}
