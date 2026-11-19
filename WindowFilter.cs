using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace PowerDimmer
{
    /// <summary>
    /// Per-window exceptions: "do not mask this window" or "only mask these windows".
    ///
    /// A rule is a plain case-insensitive substring that is matched against three
    /// things at once, so one rule can be written in whichever way is most natural:
    ///   - the process name            "notepad"       (no .exe needed)
    ///   - (part of) the exe path      "\\Tencent\\WeChat\\"
    ///   - the window title            "微信"
    /// Keeping it to one kind of rule keeps the UI to a single text box.
    /// </summary>
    public static class WindowFilter
    {
        public const string Off = "off";
        public const string Exclude = "exclude";
        public const string Only = "only";

        public static string NormalizeMode(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return Off;
            }

            return value.Trim().ToLowerInvariant() switch
            {
                "exclude" or "blacklist" or "black" => Exclude,
                "only" or "whitelist" or "white" => Only,
                _ => Off
            };
        }

        public static List<string> NormalizeKeywords(IEnumerable<string>? source)
        {
            var list = new List<string>();
            if (source == null)
            {
                return list;
            }

            foreach (var item in source)
            {
                if (string.IsNullOrWhiteSpace(item))
                {
                    continue;
                }

                var value = item.Trim();
                if (value.Length > 200)
                {
                    value = value[..200];
                }

                if (!list.Exists(x => x.Equals(value, StringComparison.OrdinalIgnoreCase)))
                {
                    list.Add(value);
                }
            }

            return list;
        }

        public static string[] SplitLines(string? text) =>
            string.IsNullOrWhiteSpace(text)
                ? Array.Empty<string>()
                : text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries);

        /// <summary>
        /// Decides whether the mask should be shown for the given foreground window.
        /// </summary>
        public static bool ShouldMask(string? mode, IReadOnlyList<string>? keywords, IntPtr hwnd)
        {
            var normalized = NormalizeMode(mode);
            if (normalized == Off)
            {
                return true;
            }

            // An enabled mode with an empty list would either hide everything ("only")
            // or mask everything ("exclude"). Treat it as off so the app never appears
            // to be broken while somebody is still typing their rules.
            if (keywords == null || keywords.Count == 0)
            {
                return true;
            }

            var hit = Matches(keywords, hwnd);
            return normalized == Only ? hit : !hit;
        }

        /// <summary>Best-effort "notepad" style name for a window, used to prefill rules.</summary>
        public static string Describe(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
            {
                return string.Empty;
            }

            Win32.GetWindowThreadProcessId(hwnd, out var pid);
            var path = Win32.TryGetProcessImagePath(pid);
            if (!string.IsNullOrEmpty(path))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (!string.IsNullOrEmpty(name))
                {
                    return name;
                }
            }

            try
            {
                using var process = Process.GetProcessById((int)pid);
                return process.ProcessName;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool Matches(IReadOnlyList<string> keywords, IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            var title = Win32.TryGetWindowTitle(hwnd);
            Win32.GetWindowThreadProcessId(hwnd, out var pid);
            var path = Win32.TryGetProcessImagePath(pid) ?? string.Empty;
            var name = path.Length > 0 ? Path.GetFileNameWithoutExtension(path) : string.Empty;

            foreach (var keyword in keywords)
            {
                if (keyword.Length == 0)
                {
                    continue;
                }

                if (title.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (path.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
