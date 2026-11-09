using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Win32;

namespace FloatingPlayer
{
    static class Log
    {
        static readonly object Gate = new object();
        static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FloatingPlayer", "app.log");

        public static void Write(string message)
        {
            lock (Gate)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                    if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 256 * 1024) File.Delete(FilePath);
                    File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff ") + message + Environment.NewLine);
                }
                catch { }
            }
        }
    }

    // User preferences, stored per user in %APPDATA%\FloatingPlayer\settings.ini.
    class Settings
    {
        public string ColorMode = "cover";      // cover | custom
        public string CustomColor = "#2B4C6F";
        public double Opacity = 0.94;           // 0.4 to 1
        public bool Blur = true;
        public double Scale = 1.0;              // 0.8 to 1.5
        public double BarWidth = 430;           // at 100 %, 340 to 640
        public bool ShowVisualizer = true;
        public double Sensitivity = 1.0;        // 0.5 to 1.6
        public bool ShowProgress = true;
        public bool AlwaysOnTop = true;
        public bool LockPosition = false;
        public string HideMode = "nomedia";     // never | nomedia | paused
        public bool LaunchAtStartup = true;
        public bool HasPosition = false;
        public double X = 0;
        public double Y = 0;

        static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FloatingPlayer", "settings.ini");

        static FieldInfo[] Fields
        {
            get { return typeof(Settings).GetFields(BindingFlags.Public | BindingFlags.Instance); }
        }

        public void CopyFrom(Settings other)
        {
            foreach (var f in Fields) f.SetValue(this, f.GetValue(other));
        }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    var field = Fields.FirstOrDefault(f => f.Name == key);
                    if (field == null) continue;
                    string value = line.Substring(eq + 1).Trim();
                    if (field.FieldType == typeof(string)) field.SetValue(s, value);
                    else if (field.FieldType == typeof(bool)) field.SetValue(s, value == "true");
                    else if (field.FieldType == typeof(double))
                    {
                        double d;
                        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) field.SetValue(s, d);
                    }
                }
            }
            catch (Exception e) { Log.Write("Settings load: " + e.Message); }
            s.Normalize();
            return s;
        }

        public void Save()
        {
            Normalize();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllLines(FilePath, Fields.Select(f =>
                {
                    object v = f.GetValue(this);
                    string text = v is bool ? ((bool)v ? "true" : "false")
                                : v is double ? ((double)v).ToString("R", CultureInfo.InvariantCulture)
                                : Convert.ToString(v);
                    return f.Name + "=" + text;
                }));
            }
            catch (Exception e) { Log.Write("Settings save: " + e.Message); }
        }

        public void Normalize()
        {
            Opacity = Clamp(Opacity, 0.4, 1);
            Scale = Clamp(Scale, 0.8, 1.5);
            BarWidth = Clamp(BarWidth, 340, 640);
            Sensitivity = Clamp(Sensitivity, 0.5, 1.6);
            if (ColorMode != "custom") ColorMode = "cover";
            if (new[] { "never", "nomedia", "paused" }.All(m => m != HideMode)) HideMode = "nomedia";
            System.Windows.Media.Color unused;
            if (!ColorMath.TryParse(CustomColor, out unused)) CustomColor = "#2B4C6F";
        }

        static double Clamp(double v, double min, double max)
        {
            return double.IsNaN(v) ? min : Math.Max(min, Math.Min(max, v));
        }
    }

    // "Launch with Windows". The MSI registers the app for every user (HKLM Run, with --autostart);
    // turning it off is then a per-user setting checked at launch. Without the MSI entry the app
    // manages its own HKCU Run value.
    static class Startup
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "FloatingPlayer";

        public static void Apply(bool enabled)
        {
            bool machineWide = false;
            try
            {
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var key = hklm.OpenSubKey(RunKey))
                    machineWide = key != null && key.GetValue(ValueName) != null;
            }
            catch { }

            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (enabled && !machineWide)
                        key.SetValue(ValueName, "\"" + Assembly.GetExecutingAssembly().Location + "\" --autostart");
                    else
                        key.DeleteValue(ValueName, false);
                }
            }
            catch (Exception e) { Log.Write("Startup: " + e.Message); }
        }
    }
}
