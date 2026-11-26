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
        public bool SideButtonsActAsMiddle = true;
        public bool SmoothScrolling = true;
        public bool LockEnabled = true;
        public int LockWindowMs = ScrollSettings.DefaultLockWindowMs;
        public bool Reverse;
        public bool SnapToAxis;
        public bool Momentum;
        public bool Paused;
        public double Speed = 1;

        private const int CurrentSettingsSchemaVersion = 1;

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
                var settings = Properties.Settings.Default;
                // Assembly versions have separate user.config files. Import the previous version
                // only when there is no current file.
                if (!config.HasFile)
                {
                    settings.Upgrade();
                    config = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.PerUserRoamingAndLocal);
                }
                var section = config.GetSection("userSettings/MarbleScroll.Properties.Settings") as ClientSettingsSection;
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
                            (property.PropertyType == typeof(int) && !int.TryParse(value, out _)) ||
                            (entry.Name == nameof(settings.ScrollSpeed) && !IsSpeed(value)))
                            throw new ConfigurationErrorsException("Invalid MarbleScroll setting: " + entry.Name);
                    }
                }

                if (settings.SettingsSchemaVersion < CurrentSettingsSchemaVersion)
                {
                    var factory = new ScrollOptions();
                    settings.SideButtonsActAsMiddle = factory.SideButtonsActAsMiddle;
                    settings.LockEnabled = factory.LockEnabled;
                    settings.LockWindowMs = factory.LockWindowMs;
                    settings.SnapToAxis = factory.SnapToAxis;
                    settings.SettingsSchemaVersion = CurrentSettingsSchemaVersion;
                    try
                    {
                        settings.Save();
                    }
                    catch (Exception error)
                    {
                        Debug.WriteLine(error);
                    }
                }

                return new ScrollOptions
                {
                    EnableMiddleButtonScroll = settings.EnableMiddleButtonScroll,
                    SideButtonsActAsMiddle = settings.SideButtonsActAsMiddle,
                    LockWindowMs = ScrollSettings.BoundLockWindow(settings.LockWindowMs),
                    SmoothScrolling = settings.SmoothScrolling,
                    LockEnabled = settings.LockEnabled,
                    Reverse = settings.Reverse,
                    SnapToAxis = settings.SnapToAxis,
                    Momentum = settings.Momentum,
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
