using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace PowerDimmer
{
    /// <summary>
    /// Portable settings store that replaces the Config.Net / Config.Net.Json stack.
    ///
    /// Why this class exists
    /// ---------------------
    /// Config.Net.Json writes settings.json by creating a scratch file in %TEMP% and then
    /// calling File.Replace(), which maps to Win32 ReplaceFile(). ReplaceFile() fails with
    /// ERROR_NOT_SAME_DEVICE (1177) whenever the scratch file and the target file are on
    /// different volumes - for example %TEMP% on a RAM disk (R:) while the app lives on D:.
    /// The exception is thrown from the App constructor, before any window exists, so the
    /// process dies silently with 0xe0434352 and the user only sees "it won't start".
    ///
    /// Rules used here
    /// ---------------
    /// 1. %TEMP% is never touched. The scratch file is settings.json.new, right next to
    ///    settings.json, so the rename can never cross a volume boundary.
    /// 2. Overwrite uses File.Move(src, dst, overwrite: true). Unlike ReplaceFile it does not
    ///    require the same volume and does not throw 1177.
    /// 3. If the portable folder is read-only (Program Files, a read-only USB stick, ...),
    ///    fall back to %APPDATA%\PowerDimmer\settings.json.
    /// 4. Every IO call is wrapped in try/catch. Losing settings is annoying; crashing the
    ///    app because of them is unacceptable.
    /// </summary>
    public sealed class Settings : ISettings
    {
        private const string FileName = "settings.json";
        private const string ScratchSuffix = ".new";
        private const int SaveDebounceMs = 400;

        // Shared with the tray menu's "custom..." dialog so the limits the user sees and
        // the limits enforced on load are literally the same numbers.
        public const int SwitchDelayMinMs = 0;
        public const int SwitchDelayMaxMs = 5000;
        public const int FadeOutMinMs = 0;
        public const int FadeOutMaxMs = 5000;

        // How many of the most recently used windows stay un-dimmed. 1 is the classic
        // behaviour (only the window in front); the tray menu offers 1..8.
        public const int KeepBrightMin = 1;
        public const int KeepBrightMax = 8;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            // The read-only legacy "brightness" field must never be written back as null.
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        private readonly object gate = new();
        private readonly string path;
        private Timer? saveTimer;

        private bool activeOnLaunch = true;
        private bool dimmingEnabled = false;

        // Off by default: pushing the shell taskbar to the bottom of the z-order is a
        // side effect a lot of people do not want (it hides the clock and the tray).
        private bool dimTaskbar = false;
        private bool undimOnDesktop = false;
        private bool multiMonitorDimming = true;

        // "Dim level" is the opposite of the old "brightness" value: 100 = solid black
        // mask, 0 = no mask at all. The slider in the tray menu shows this number, so
        // dragging it to the right now means "more mask", which is what people expect.
        private int dimLevel = 100;
        private bool windowShadeEnabled = true;
        private string maskColor = "#000000";
        private string language = Loc.Auto;

        // Per-window exceptions. "off" keeps the original behaviour of masking
        // everything; see WindowFilter for what a rule matches against.
        private string filterMode = WindowFilter.Off;
        private List<string> filterKeywords = new();

        // How long the window we just left stays un-dimmed after the focus moves.
        // 0 = move the mask immediately (old behaviour). See App.UpdateDimming.
        private int switchDelayMs = 300;

        // How long the window we just left takes to fade to the current dim level instead
        // of snapping dark. 0 = hard cut, which is what the mask used to do.
        private int fadeOutMs = 250;

        // Cursor spotlight: punches a soft hole in the mask around the mouse pointer so
        // the area under it stays readable. Radius is in DIPs (scales with the monitor).
        private bool spotlightEnabled = false;
        private double spotlightRadius = 140;

        // Number of most recently used windows that stay readable at once.
        private int keepBrightCount = 1;

        public event PropertyChangedEventHandler? PropertyChanged;

        public Settings()
        {
            path = ResolvePath(out var loaded);
            if (loaded != null)
            {
                Apply(loaded);

                // A file written by the old "brightness" build gets rewritten in the new
                // format right away, so the conversion only ever happens once.
                if (loaded.dimLevel == null && loaded.brightness != null)
                {
                    ScheduleSave();
                }
            }
        }

        /// <summary>Full path of the file that is actually being written.</summary>
        public string FilePath => path;

        public bool ActiveOnLaunch
        {
            get => activeOnLaunch;
            set => Set(ref activeOnLaunch, value);
        }

        public bool DimmingEnabled
        {
            get => dimmingEnabled;
            set => Set(ref dimmingEnabled, value);
        }

        public bool DimTaskbar
        {
            get => dimTaskbar;
            set => Set(ref dimTaskbar, value);
        }

        public bool UndimOnDesktop
        {
            get => undimOnDesktop;
            set => Set(ref undimOnDesktop, value);
        }

        public bool MultiMonitorDimming
        {
            get => multiMonitorDimming;
            set => Set(ref multiMonitorDimming, value);
        }

        public int DimLevel
        {
            get => dimLevel;
            set => Set(ref dimLevel, Math.Clamp(value, 0, 100));
        }

        public bool WindowShadeEnabled
        {
            get => windowShadeEnabled;
            set => Set(ref windowShadeEnabled, value);
        }

        /// <summary>Mask colour as #RRGGBB. Anything unparsable falls back to black.</summary>
        public string MaskColor
        {
            get => maskColor;
            set => Set(ref maskColor, NormalizeColor(value));
        }

        /// <summary>"auto", "zh" or "en".</summary>
        public string Language
        {
            get => language;
            set => Set(ref language, Loc.Normalize(value));
        }

        /// <summary>"off", "exclude" (skip matching windows) or "only" (mask only those).</summary>
        public string FilterMode
        {
            get => filterMode;
            set => Set(ref filterMode, WindowFilter.NormalizeMode(value));
        }

        /// <summary>
        /// Grace period after a focus change, in milliseconds: the mask stays under the
        /// previous window for this long, so both windows are readable during the
        /// transition. 0 disables it.
        /// </summary>
        public int SwitchDelayMs
        {
            get => switchDelayMs;
            set => Set(ref switchDelayMs, Math.Clamp(value, SwitchDelayMinMs, SwitchDelayMaxMs));
        }

        /// <summary>
        /// How long the window we just left takes to fade to the current dim level, in
        /// milliseconds. 0 = hard cut (the mask is a SetWindowPos, it cannot animate).
        /// </summary>
        public int FadeOutMs
        {
            get => fadeOutMs;
            set => Set(ref fadeOutMs, Math.Clamp(value, FadeOutMinMs, FadeOutMaxMs));
        }

        /// <summary>Keep the area around the mouse pointer un-dimmed.</summary>
        public bool SpotlightEnabled
        {
            get => spotlightEnabled;
            set => Set(ref spotlightEnabled, value);
        }

        /// <summary>Spotlight hole radius in DIPs (96 = one inch at 100% scaling).</summary>
        public double SpotlightRadius
        {
            get => spotlightRadius;
            set => Set(ref spotlightRadius, Math.Clamp(value, 20, 1200));
        }

        /// <summary>
        /// How many of the most recently used windows stay un-dimmed. 1 = only the
        /// focused window, which is what the mask used to do.
        /// </summary>
        public int KeepBrightCount
        {
            get => keepBrightCount;
            set => Set(ref keepBrightCount, Math.Clamp(value, KeepBrightMin, KeepBrightMax));
        }

        /// <summary>
        /// Case-insensitive substrings matched against process name, exe path and
        /// window title. Assigning a new list triggers a change notification, which is
        /// what makes the mask re-evaluate immediately.
        /// </summary>
        public List<string> FilterKeywords
        {
            get => filterKeywords;
            set
            {
                var cleaned = WindowFilter.NormalizeKeywords(value);
                if (cleaned.Count == filterKeywords.Count)
                {
                    var same = true;
                    for (var i = 0; i < cleaned.Count; i++)
                    {
                        if (!string.Equals(cleaned[i], filterKeywords[i], StringComparison.OrdinalIgnoreCase))
                        {
                            same = false;
                            break;
                        }
                    }

                    if (same)
                    {
                        filterKeywords = cleaned;
                        return;
                    }
                }

                filterKeywords = cleaned;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FilterKeywords)));
                ScheduleSave();
            }
        }

        /// <summary>
        /// Re-reads settings.json and applies it on top of the current state.
        ///
        /// Deliberately not a FileSystemWatcher: this app never watches the disk in the
        /// background, the reload is an explicit menu action. Returns false when the file
        /// is missing or not valid JSON, in which case nothing is changed.
        /// </summary>
        public bool Reload()
        {
            var dto = TryLoad(path);
            if (dto == null)
            {
                return false;
            }

            // Go through the properties so every change raises PropertyChanged and the
            // mask / tray menu update themselves.
            DimmingEnabled = dto.dimmingEnabled ?? dimmingEnabled;
            ActiveOnLaunch = dto.activeOnLaunch ?? activeOnLaunch;
            DimTaskbar = dto.dimTaskbar ?? dimTaskbar;
            UndimOnDesktop = dto.undimOnDesktop ?? undimOnDesktop;
            MultiMonitorDimming = dto.multiMonitorDimming ?? multiMonitorDimming;
            WindowShadeEnabled = dto.windowShadeEnabled ?? windowShadeEnabled;
            MaskColor = dto.maskColor ?? maskColor;
            FilterMode = dto.filterMode ?? filterMode;
            FilterKeywords = dto.filterKeywords ?? filterKeywords;

            if (dto.switchDelayMs != null)
            {
                SwitchDelayMs = dto.switchDelayMs.Value;
            }

            if (dto.fadeOutMs != null)
            {
                FadeOutMs = dto.fadeOutMs.Value;
            }

            if (dto.spotlightEnabled != null)
            {
                SpotlightEnabled = dto.spotlightEnabled.Value;
            }

            if (dto.spotlightRadius != null)
            {
                SpotlightRadius = dto.spotlightRadius.Value;
            }

            if (dto.keepBrightCount != null)
            {
                KeepBrightCount = dto.keepBrightCount.Value;
            }

            var level = dto.dimLevel ?? (dto.brightness != null ? 100 - dto.brightness.Value : (int?)null);
            if (level != null)
            {
                DimLevel = level.Value;
            }

            // Language is resolved by the caller: switching it rebuilds the tray menu.
            language = Loc.Normalize(dto.language ?? language);
            return true;
        }

        private static string NormalizeColor(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "#000000";
            }

            var v = value.Trim();
            if (!v.StartsWith("#", StringComparison.Ordinal))
            {
                v = "#" + v;
            }

            return v.Length == 7 || v.Length == 9 ? v : "#000000";
        }

        /// <summary>
        /// Cancels the pending debounce and writes immediately. Called on exit so a slider
        /// drag followed by "Exit" still lands on disk.
        /// </summary>
        public void Flush()
        {
            lock (gate)
            {
                try
                {
                    saveTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                }
                catch (ObjectDisposedException)
                {
                }
            }

            WriteToDisk();
        }

        private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            ScheduleSave();
        }

        private void ScheduleSave()
        {
            lock (gate)
            {
                saveTimer ??= new Timer(_ => WriteToDisk(), null, Timeout.Infinite, Timeout.Infinite);
                try
                {
                    saveTimer.Change(SaveDebounceMs, Timeout.Infinite);
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        private void WriteToDisk()
        {
            lock (gate)
            {
                string? scratch = null;
                try
                {
                    var directory = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    // Scratch file lives in the same folder: the rename below can never
                    // become a cross-volume move, which is what used to crash the app.
                    scratch = path + ScratchSuffix;
                    File.WriteAllText(scratch, JsonSerializer.Serialize(ToDto(), JsonOptions), new UTF8Encoding(false));
                    File.Move(scratch, path, overwrite: true);
                    scratch = null;
                }
                catch
                {
                    // Persisting settings is best-effort. Never let it reach the dispatcher.
                }
                finally
                {
                    if (scratch != null)
                    {
                        try
                        {
                            File.Delete(scratch);
                        }
                        catch
                        {
                        }
                    }
                }
            }
        }

        private static string ResolvePath(out SettingsDto? loaded)
        {
            var portableDirectory = AppContext.BaseDirectory;
            var portable = Path.Combine(portableDirectory, FileName);

            loaded = TryLoad(portable);
            if (IsDirectoryWritable(portableDirectory))
            {
                return portable;
            }

            // Read-only install folder: keep reading the portable file if it exists, but
            // write to %APPDATA% from now on.
            var roaming = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "PowerDimmer",
                FileName);

            loaded ??= TryLoad(roaming);
            return roaming;
        }

        private static bool IsDirectoryWritable(string directory)
        {
            try
            {
                if (string.IsNullOrEmpty(directory))
                {
                    return false;
                }

                Directory.CreateDirectory(directory);
                var probe = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".wtest");
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
                {
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static SettingsDto? TryLoad(string file)
        {
            try
            {
                if (!File.Exists(file))
                {
                    return null;
                }

                var json = File.ReadAllText(file, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return null;
                }

                return JsonSerializer.Deserialize<SettingsDto>(json, JsonOptions);
            }
            catch
            {
                return null;
            }
        }

        private void Apply(SettingsDto dto)
        {
            activeOnLaunch = dto.activeOnLaunch ?? activeOnLaunch;
            dimmingEnabled = dto.dimmingEnabled ?? dimmingEnabled;
            dimTaskbar = dto.dimTaskbar ?? dimTaskbar;
            undimOnDesktop = dto.undimOnDesktop ?? undimOnDesktop;
            multiMonitorDimming = dto.multiMonitorDimming ?? multiMonitorDimming;
            windowShadeEnabled = dto.windowShadeEnabled ?? windowShadeEnabled;

            // Migration: older builds stored "brightness" where 0 meant solid black
            // (opacity = 1 - brightness / 100). Convert it once so an existing
            // settings.json keeps looking exactly the same.
            var level = dto.dimLevel;
            if (level == null && dto.brightness != null)
            {
                level = 100 - dto.brightness.Value;
            }

            dimLevel = Math.Clamp(level ?? dimLevel, 0, 100);
            maskColor = NormalizeColor(dto.maskColor ?? maskColor);
            language = Loc.Normalize(dto.language ?? language);
            filterMode = WindowFilter.NormalizeMode(dto.filterMode ?? filterMode);
            filterKeywords = WindowFilter.NormalizeKeywords(dto.filterKeywords ?? filterKeywords);
            switchDelayMs = Math.Clamp(dto.switchDelayMs ?? switchDelayMs, SwitchDelayMinMs, SwitchDelayMaxMs);
            fadeOutMs = Math.Clamp(dto.fadeOutMs ?? fadeOutMs, FadeOutMinMs, FadeOutMaxMs);
            spotlightEnabled = dto.spotlightEnabled ?? spotlightEnabled;
            spotlightRadius = Math.Clamp(dto.spotlightRadius ?? spotlightRadius, 20, 1200);
            keepBrightCount = Math.Clamp(dto.keepBrightCount ?? keepBrightCount, KeepBrightMin, KeepBrightMax);
        }

        private SettingsDto ToDto() => new()
        {
            activeOnLaunch = activeOnLaunch,
            dimmingEnabled = dimmingEnabled,
            dimTaskbar = dimTaskbar,
            undimOnDesktop = undimOnDesktop,
            multiMonitorDimming = multiMonitorDimming,
            dimLevel = dimLevel,
            windowShadeEnabled = windowShadeEnabled,
            maskColor = maskColor,
            language = language,
            filterMode = filterMode,
            filterKeywords = filterKeywords,
            switchDelayMs = switchDelayMs,
            fadeOutMs = fadeOutMs,
            spotlightEnabled = spotlightEnabled,
            spotlightRadius = spotlightRadius,
            keepBrightCount = keepBrightCount
        };

        // Same key names the Config.Net version produced, so an existing settings.json
        // keeps working untouched. "brightness" is only read, never written any more.
        private sealed class SettingsDto
        {
            public bool? activeOnLaunch { get; set; }
            public bool? dimmingEnabled { get; set; }
            public bool? dimTaskbar { get; set; }
            public bool? undimOnDesktop { get; set; }
            public bool? multiMonitorDimming { get; set; }
            public int? dimLevel { get; set; }
            public int? brightness { get; set; }
            public bool? windowShadeEnabled { get; set; }
            public string? maskColor { get; set; }
            public string? language { get; set; }
            public string? filterMode { get; set; }
            public List<string>? filterKeywords { get; set; }
            public int? switchDelayMs { get; set; }
            public int? fadeOutMs { get; set; }
            public bool? spotlightEnabled { get; set; }
            public double? spotlightRadius { get; set; }
            public int? keepBrightCount { get; set; }
        }
    }
}
