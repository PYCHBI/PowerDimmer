using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace PowerDimmer
{
    /// <summary>
    /// Single place that answers "which build is this?".
    ///
    /// The number itself lives in PowerDimmer.csproj (&lt;Version&gt;), so it ends up in
    /// three places at once: the About dialog, the tray menu, and the Windows
    /// "文件属性 → 详细信息" page of PowerDimmer.exe.
    /// </summary>
    public static class AppInfo
    {
        public const string Name = "PowerDimmer";

        public const string ProjectUrl = "https://github.com/PYCHBI/PowerDimmer";

        // This repository is a fork of a fork: the version it started from is shtuseriff's,
        // which in turn forked shayne's original. Both are credited, neither is "unnamed".
        public const string BaselineUrl = "https://github.com/shtuseriff/PowerDimmer";

        public const string UpstreamUrl = "https://github.com/shayne/PowerDimmer";

        public const string License = "MIT";

        /// <summary>e.g. "1.1.0". Never null, never empty.</summary>
        public static string Version { get; } = ReadVersion();

        /// <summary>e.g. ".NET 8.0.22".</summary>
        public static string Runtime { get; } =
            System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;

        /// <summary>Absolute path of the exe that is currently running.</summary>
        public static string ExePath
        {
            get
            {
                try
                {
                    return Environment.ProcessPath ?? Assembly.GetExecutingAssembly().Location;
                }
                catch
                {
                    return string.Empty;
                }
            }
        }

        private static string ReadVersion()
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();

                // The SDK stamps InformationalVersion and may append "+<commit sha>";
                // only the human part is interesting here.
                var informational = assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                    .InformationalVersion;
                if (!string.IsNullOrWhiteSpace(informational))
                {
                    var plus = informational.IndexOf('+');
                    return plus > 0 ? informational[..plus] : informational;
                }

                var version = assembly.GetName().Version;
                return version == null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
            }
            catch
            {
                return "0.0.0";
            }
        }

        /// <summary>Plain-text block for the "copy" button - handy when filing an issue.</summary>
        public static string Describe()
        {
            return string.Join(Environment.NewLine, new[]
            {
                $"{Name} {Version}",
                $"runtime: {Runtime}",
                $"exe: {ExePath}",
                $"os: {Environment.OSVersion.VersionString}",
                ProjectUrl
            });
        }

        public static void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch
            {
            }
        }
    }
}
