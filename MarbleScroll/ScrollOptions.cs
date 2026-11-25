using System;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace MarbleScroll
{
    internal sealed class ScrollOptions
    {
        internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        internal const string RunValue = "MarbleScroll";

        public bool EnableMiddleButtonScroll = true;
        public bool EnableXButtonScroll = true;
        public bool ConvertXToMiddleClick;
        public bool SmoothScrolling = true;
        public bool LockEnabled;
        public bool Reverse;
        public bool SnapToAxis = true;
        public bool Paused;
        public double Speed = 1;

        public ScrollOptions Copy() { return (ScrollOptions)MemberwiseClone(); }

        public static ScrollOptions Load(bool defaults)
        {
            if (defaults)
                return new ScrollOptions();

            string path = null;
            try
            {
                Configuration config = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.PerUserRoamingAndLocal);
                path = config.FilePath;
                var section = config.GetSection("userSettings/MarbleScroll.Properties.Settings") as ClientSettingsSection;
                var settings = Properties.Settings.Default;
                // The settings provider can silently substitute defaults for malformed values.
                if (section != null)
                {
                    foreach (SettingElement entry in section.Settings)
                    {
                        SettingsProperty property = settings.Properties[entry.Name];
                        if (property == null)
                            continue;
                        string value = entry.Value.ValueXml?.InnerText;
                        if ((property.PropertyType == typeof(bool) && !bool.TryParse(value, out _)) ||
                            (entry.Name == nameof(settings.ScrollSpeed) && !IsSpeed(value)))
                            throw new ConfigurationErrorsException("Invalid MarbleScroll setting: " + entry.Name);
                    }
                }

                return new ScrollOptions
                {
                    EnableMiddleButtonScroll = settings.EnableMiddleButtonScroll,
                    EnableXButtonScroll = settings.EnableXButtonScroll,
                    ConvertXToMiddleClick = settings.ConvertXToMiddleClick,
                    SmoothScrolling = settings.SmoothScrolling,
                    LockEnabled = settings.LockEnabled,
                    Reverse = settings.Reverse,
                    SnapToAxis = settings.SnapToAxis,
                    Paused = settings.Paused,
                    Speed = ParseSpeed(settings.ScrollSpeed)
                };
            }
            catch (ConfigurationErrorsException error)
            {
                Debug.WriteLine(error);
                Quarantine(path, error);
            }
            catch (Exception error)
            {
                Debug.WriteLine(error);
            }
            return new ScrollOptions();
        }

        private static void Quarantine(string path, ConfigurationErrorsException error)
        {
            try
            {
                if (!string.IsNullOrEmpty(error.Filename) &&
                    string.Equals(Path.GetFileName(error.Filename), "user.config", StringComparison.OrdinalIgnoreCase))
                    path = error.Filename;
                if (string.Equals(Path.GetFileName(path), "user.config", StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(path))
                {
                    File.Move(path, path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff"));
                    ConfigurationManager.RefreshSection("userSettings/MarbleScroll.Properties.Settings");
                    Properties.Settings.Default.Reload();
                }
            }
            catch (Exception renameError)
            {
                Debug.WriteLine(renameError);
            }
        }

        private static bool IsSpeed(string value)
        {
            return value == "0.5" || value == "1" || value == "2" || value == "3";
        }

        private static double ParseSpeed(string value)
        {
            if (!IsSpeed(value))
                throw new ConfigurationErrorsException("Invalid MarbleScroll scroll speed.");
            return double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
