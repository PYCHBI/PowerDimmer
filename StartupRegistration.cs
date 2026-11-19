using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace PowerDimmer
{
    /// <summary>
    /// "Start with Windows" for a portable tool: writes a single value under
    /// HKCU\Software\Microsoft\Windows\CurrentVersion\Run so nothing is installed
    /// system-wide and no admin rights are needed.
    /// </summary>
    public static class StartupRegistration
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "PowerDimmer";

        public static string ExecutablePath
        {
            get
            {
                try
                {
                    // ProcessPath points at the real exe even after a single-file publish.
                    var path = Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    {
                        return path;
                    }
                }
                catch
                {
                }

                return Path.Combine(AppContext.BaseDirectory, "PowerDimmer.exe");
            }
        }

        public static bool IsEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
                var value = key?.GetValue(ValueName) as string;
                return !string.IsNullOrWhiteSpace(value) &&
                       value!.Trim('"').Equals(ExecutablePath, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Returns the state that is actually in effect afterwards.</summary>
        public static bool SetEnabled(bool enabled)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
                if (key == null)
                {
                    return IsEnabled();
                }

                if (enabled)
                {
                    key.SetValue(ValueName, "\"" + ExecutablePath + "\"", RegistryValueKind.String);
                }
                else
                {
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
                }
            }
            catch
            {
                // Locked-down machine: report whatever is really there.
            }

            return IsEnabled();
        }

        public static void OpenConfigFolder(string? filePath)
        {
            try
            {
                if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + filePath + "\"")
                    {
                        UseShellExecute = true
                    });
                    return;
                }

                var folder = !string.IsNullOrEmpty(filePath)
                    ? Path.GetDirectoryName(filePath)!
                    : AppContext.BaseDirectory;

                Directory.CreateDirectory(folder);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + folder + "\"")
                {
                    UseShellExecute = true
                });
            }
            catch
            {
            }
        }
    }
}
