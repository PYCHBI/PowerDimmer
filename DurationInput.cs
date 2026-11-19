using System;
using System.Globalization;

namespace PowerDimmer
{
    /// <summary>
    /// Parses the free-form duration the tray menu accepts for "switch delay" and
    /// "fade out".
    ///
    /// Accepted forms - the unit is never ambiguous, so nobody has to guess:
    ///   350        -> 350 ms   (bare number without a decimal point = milliseconds)
    ///   350ms      -> 350 ms
    ///   350 毫秒    -> 350 ms
    ///   0.35       -> 350 ms   (number with a decimal point = seconds)
    ///   0.35s      -> 350 ms
    ///   1.5 秒     -> 1500 ms
    ///   0          -> 0        (off)
    ///
    /// Full-width digits and the Chinese full stop are normalised first, because the
    /// input box is reached from a Chinese IME on a Chinese desktop.
    /// </summary>
    public static class DurationInput
    {
        public static bool TryParse(string? text, int minMs, int maxMs, out int milliseconds)
        {
            milliseconds = 0;
            var raw = Normalize(text);
            if (raw.Length == 0)
            {
                return false;
            }

            double factor;
            string number;

            if (StripSuffix(raw, "毫秒", out number) ||
                StripSuffix(raw, "msec", out number) ||
                StripSuffix(raw, "ms", out number))
            {
                factor = 1;
            }
            else if (StripSuffix(raw, "秒钟", out number) ||
                     StripSuffix(raw, "秒", out number) ||
                     StripSuffix(raw, "secs", out number) ||
                     StripSuffix(raw, "sec", out number) ||
                     StripSuffix(raw, "s", out number))
            {
                factor = 1000;
            }
            else
            {
                number = raw;
                // No unit at all: "0.35" reads as seconds, "350" as milliseconds. That
                // keeps both "type a plain ms value" and "type 0.35" working.
                factor = number.IndexOf('.') >= 0 ? 1000 : 1;
            }

            if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return false;
            }

            var ms = value * factor;
            if (double.IsNaN(ms) || double.IsInfinity(ms) || ms < minMs || ms > maxMs)
            {
                return false;
            }

            milliseconds = (int)Math.Round(ms, MidpointRounding.AwayFromZero);
            return true;
        }

        /// <summary>Trim, lower-case, and map full-width characters to ASCII.</summary>
        private static string Normalize(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var chars = text.Trim().ToLowerInvariant().ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                var c = chars[i];
                if (c >= '０' && c <= '９')
                {
                    chars[i] = (char)('0' + (c - '０'));
                }
                else if (c == '．' || c == '。' || c == ',' || c == '，')
                {
                    chars[i] = '.';
                }
                else if (c == '　')
                {
                    chars[i] = ' ';
                }
            }

            return new string(chars).Trim();
        }

        private static bool StripSuffix(string text, string suffix, out string rest)
        {
            if (text.EndsWith(suffix, StringComparison.Ordinal) && text.Length > suffix.Length)
            {
                rest = text.Substring(0, text.Length - suffix.Length).Trim();
                return rest.Length > 0;
            }

            rest = text;
            return false;
        }

        /// <summary>"0.35 秒" / "1 秒" - used in the menu caption.</summary>
        public static string Describe(int milliseconds)
        {
            var text = milliseconds % 1000 == 0
                ? (milliseconds / 1000).ToString(CultureInfo.InvariantCulture)
                : (milliseconds / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);

            return text + " " + Loc.T("Unit.Seconds");
        }
    }
}
